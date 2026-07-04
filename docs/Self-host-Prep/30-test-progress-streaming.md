# Phase 30 - Per-Test Progress Streaming (`stark test --test-progress`)

Status: **fully implemented at component scope (2026-07-03, uncommitted on
`self-host-prep`).** Stage0: driver flags, streaming, timeout, generator
emission. Contract artifacts: the `tests/fixtures/test-progress` fixtures
and goldens are normative, enforced by the stage0
`TestProgressProtocolTests` harness. Stage1: `Compiler.TestRunner`
(generated-runner emission, byte-identical to stage0) and
`Compiler.TestDriver` (streaming/prefix/timeout execution, golden-parity
verified), both pinned by green `tests-stark/selfhost.TestRunner` facts,
over the new `System.Process` spawned-child surfaces. The eventual stage1
`stark test` CLI port composes these components; that wiring is tracked
with the CLI port in TASKS.md, not here. This document remains the design
record and the cross-stage protocol contract.

Related: `docs/Internals/CompilerDevelopmentVerification.md` ("Test Runner
Progress"), `TASKS.md` (per-fact streaming entry + stage1 contract entry under
the `stark test` port), `04-test-infrastructure-audit.md`,
`22-threading-coordination.md` (channels are the blessed primitive if the
driver ever goes parallel).

## 1. Problem

`stark test` runs a generated runner executable per test project. Before this
phase the driver read the runner's stdout/stderr with `ReadToEndAsync`-style
buffering: **nothing was shown until the process exited**. Consequences:

- A timed-out or killed run (SIGKILL from a harness deadline, SIGTRAP exit 133
  from a range violation) produced **zero output** — no way to tell which fact
  was in flight, how far the run got, or whether it hung versus ran slowly.
- Full suites run 15-47 minutes; the mega-main runner (one giant generated
  `main()`) makes single facts occasionally explode super-linear compiler
  passes. Distinguishing "hung" from "the 17-minute compile again" required
  killing the run and losing the evidence.
- Per-fact `ok <name>` lines existed but arrived as one batch, so they carried
  no timing information.

Goals, in priority order:

1. Partial output must survive a kill/timeout (streaming, not buffering).
2. A hung run's transcript must **name the fact in flight** (a result line
   only proves a fact *finished*; a start marker proves one *started*).
3. Per-fact wall-clock must be recoverable from the transcript.
4. A deadline must kill the run and say so explicitly, instead of the harness
   hanging or the caller guessing.
5. Default output stays **byte-identical** to the legacy format — existing
   transcript-scraping tooling and ledger habits must not break.
6. The mechanism must be a **stage contract**: stage1's `stark test` port
   reproduces the same observable behavior.

## 2. Design Overview

Three layers, split so that the maximum amount of the mechanism lives in
shared Stark code (ported for free) and the minimum lives in the driver
(ported twice):

```
stark test --test-progress --test-timeout 120
  │
  ├─ driver (stage0: ProjectCliDriver.cs; stage1: future stark test port)
  │    passes --progress in runner argv; streams lines as they arrive;
  │    stamps [12.3s] elapsed prefixes; enforces the timeout by killing
  │    the process tree
  │
  ├─ generated runner (stage0: StarkTestRunnerGenerator.cs; stage1: future
  │    runner-generator port) — parses --progress from argv; wraps every
  │    entry in BeginFact + RunFactCounted with (ordinal, total)
  │
  └─ System.Testing (stdlib/src/System/Testing.stark — SHARED, already Stark)
       BeginFact prints `run <name>`; RunFactCounted prints
       `ok|FAILED <name> (k/N)`; both no-op/degrade to legacy RunFact
       when progress is off
```

Key decisions:

- **The protocol is text lines over the existing stdout/stderr pipes.** No
  side channel, no IPC, no temp files. The runner stays a plain executable
  that is still useful run by hand.
- **The runner needs no clock.** Elapsed-time prefixes are stamped by the
  driver at line arrival. This keeps `System.Testing` free of time syscalls
  and makes the prefixes uniform across runner implementations.
- **Progress is opt-in at two levels.** The driver flag (`--test-progress`)
  controls prefixes and forwards the runner flag (`--progress`); the runner
  flag controls markers/counters. Running the executable by hand with
  `--progress` works without the driver.
- **Streaming is unconditional.** Even without `--test-progress` the driver
  forwards lines as they arrive (it never buffers until exit). The flag only
  adds markers, counters, and prefixes. Goal 1 therefore holds for every run.
- **Timeout is a whole-run deadline,** not per-fact. Per-fact deadlines would
  require the runner to self-instrument with a clock and a watchdog thread;
  the whole-run deadline plus start markers already answers "which fact hung".
  Per-fact timeouts are an explicit non-goal for now (§8).

## 3. Protocol Specification (cross-stage contract)

These are the observable behaviors stage1 must reproduce. Everything not
listed here is implementation detail.

### 3.1 Runner argv

- Every process argument after the executable name is a collection-name
  filter, except the literal flags `--list-collections` and `--progress`.
- `--progress` sets the runner's progress mode. It is position-independent
  and composes with collection filters.
- Unknown arguments keep their existing behavior (reported as unknown
  collections; exit 1).

### 3.2 Runner output, progress off

Byte-identical to the pre-phase format:

- `ok <name>` on stdout for a passing fact.
- `FAILED <name>` on stderr for a failing fact.
- `skipped <name>: <reason>` on stdout for a skipped fact.
- Exit code 0 iff no fact failed.

### 3.3 Runner output, progress on

- Immediately before invoking each non-skipped entry: `run <name>` on
  **stdout**.
- After the entry returns: `ok <name> (k/N)` on **stdout**, or
  `FAILED <name> (k/N)` on **stderr**.
- `k` is the entry's 1-based ordinal over the project's **generated entries**
  (post `[Fact]`/`[Theory]` expansion — each theory/member-data row is its own
  entry). `N` is the total generated entry count.
- Skipped entries print the unchanged `skipped` line (no marker, no counter)
  but **do consume an ordinal**, and entries excluded by a collection filter
  consume ordinals without printing anything. Ordinals are therefore stable
  entry indexes, not a count of executed facts; under a filter the printed
  `k` values are sparse. This is intentional: a stable `k` identifies the same
  entry across filtered and unfiltered runs. (Revisit only with a
  transcript-consumer reason; see §9.)
- No other output changes. In particular `--list-collections` output and the
  unknown-collection report are unaffected by `--progress`.

### 3.4 Driver behavior (`stark test`)

- Forwards runner stdout lines to its stdout and stderr lines to its stderr
  **as they arrive**, line-buffered, never waiting for exit.
- `--test-progress`: adds `--progress` to the runner argv and prefixes every
  forwarded line with elapsed wall-clock since process start, format
  `[<seconds>.<tenths>s] ` (e.g. `[12.3s] `), invariant-culture, one decimal.
  Per-fact duration is the delta between a fact's `run` line prefix and its
  result line prefix.
- `--test-timeout <seconds>` (positive integer): at the deadline, kill the
  runner **process tree** (facts may spawn children), then print an explicit
  timeout report to stderr that points at the last `run <name>` line, and
  fail the run (exit 1). Both flags are valid only for `stark test`.
- Writes from the two streams are serialized (no interleaved partial lines).
  Cross-stream *ordering* between stdout and stderr lines is best-effort, as
  it always was for a piped child.

### 3.5 `System.Testing` surface (shared, already implemented)

```stark
public fn void BeginFact(bool progress, ascii name)
public fn u8[0 1] RunFactCounted(bool progress,
    u32[0 2 ** 31 - 1] ordinal, u32[0 2 ** 31 - 1] total,
    ascii name, bool assertion)
```

`BeginFact` no-ops when `progress` is false. `RunFactCounted` delegates to
`RunFact` when `progress` is false (this is what makes §3.2 byte-identical by
construction rather than by discipline). Stage1 consumes these unchanged —
the stdlib is compiled by whichever stage is building, so the protocol
functions port for free.

## 4. Stage0 Implementation Map (landed)

| Piece | Location | Shape |
| --- | --- | --- |
| Protocol functions | `stdlib/src/System/Testing.stark` (`BeginFact`, `RunFactCounted`) | counter rendered via `OwnedAscii` appends; falls back to `RunFact` when off |
| Runner generation | `src/Compiler/StarkTestRunnerGenerator.cs` | `stack mut bool progress = false;` + `--progress` parse in the collection-argv loop (and in a standalone loop when the project has no collections); per-entry `BeginFact(progress, "Name")` + `RunFactCounted(progress, k, N, "Name", Call())`; `entryOrdinal` counts every generated entry |
| Driver flags + session | `src/Compiler/ProjectCliDriver.cs` | `--test-progress` / `--test-timeout` parse cases, help text, `TestProgress`/`TestTimeoutSeconds` threaded through the options and session records |
| Streaming + timeout | `src/Compiler/ProjectCliDriver.cs` `RunTestExecutableAsync` | `OutputDataReceived`/`ErrorDataReceived` pumps, a `lock` write gate, `Stopwatch` prefixes, `CancellationTokenSource` deadline + `process.Kill(entireProcessTree: true)` + explicit report, final `WaitForExit()` drain before reading the exit code |
| Generator tests | `tests/compiler.Tests/StarkTestRunnerGeneratorTests.cs` | assertions updated for the counted call shape (16/16 green) |
| Docs | `docs/Internals/CompilerDevelopmentVerification.md` "Test Runner Progress" | usage + contract note |

Verified by smoke test: with the flag, `run`/`ok ... (k/N)` lines stream with
`[..s]` prefixes; without the flag, output is byte-identical to the legacy
format; a deliberate hang is killed at the deadline with the explicit report.

## 5. Stage1 Port Design

The stage1 side has two pieces, both blocked on their parent ports (see the
`stark test` port section of `TASKS.md`):

1. **Runner generator port.** When stage1 grows generated-runner creation, it
   emits the same generated-source shape: the `progress` local, the argv
   parse (both the with-collections and no-collections variants), and the
   per-entry `BeginFact` + `RunFactCounted` pair with `(ordinal, total)`
   computed over generated entries. Because the emitted code calls the shared
   `System.Testing` functions, matching the *generated call shape* is
   sufficient to match the protocol.
2. **Driver port.** The stage1 `stark test` needs: child-process spawn with
   piped stdout/stderr, incremental line reads from both pipes, a monotonic
   clock for prefixes, serialized writes, and deadline kill of the process
   tree. Map to the pre-bootstrap threading scope (`22-threading-coordination.md`):
   one payload thread per pipe publishing lines over a channel, the main
   thread consuming, stamping, and forwarding — this gives the write gate for
   free (single consumer). Deadline = channel receive with timeout, then
   process-tree kill. Audit `System.Process`/`System.Threading` for gaps
   (pipe-read, monotonic clock, kill-tree) **before** starting the port and
   fix stdlib first — the driver should not grow FFI of its own.

Parity gate for the port: the conformance fixture in §7 must produce
identical transcripts (modulo timing digits) under both drivers.

## 6. Edge Cases and Known Risks

- **Sparse ordinals under filters** (§3.3): stable entry identity was chosen
  over dense execution counts. Documented, not a bug.
- **Theories:** each member-data row is its own generated entry with its own
  `run`/result lines and ordinal — a hang inside row 7 of a theory is
  attributed to row 7, not the theory as a whole.
- **Cross-stream ordering:** a `FAILED` (stderr) line can appear displaced
  relative to neighboring stdout lines when pipes race. Result-line counters
  `(k/N)` make transcript reconstruction order-insensitive; do not "fix" this
  by merging the streams — exit-code tooling and CI split them.
- **Elapsed prefix baseline** is process start, so the first fact's prefix
  includes runner startup. Acceptable: deltas, not absolutes, carry the
  signal.
- **`RunFactCounted` renders `(k/N)` via `OwnedAscii` appends.** (Risk
  closed 2026-07-03.) The `v_cross` data-loss investigation concluded: the
  corruption was host wrong-code from LLVM allocator attributes on visible
  allocator bodies (TASKS.md §6 entry, fixed in
  `LlvmBuiltinAndHelperEmitter.cs`), not an `OwnedAscii` defect. With the
  fix, the conformance fixture's double-digit `(10/10)` counter renders
  correctly through the unchanged `OwnedAscii` path — pinned by the §3.3
  golden transcript.
- **Kill-tree on POSIX** relies on the .NET `entireProcessTree` semantics in
  stage0; stage1 must decide its own mechanism (process groups) — noted in
  the port task.

## 7. Verification Recipe

Targeted slices only (per the test-running discipline):

1. **Generator shape:** `dotnet test --filter StarkTestRunnerGenerator`
   (asserts the emitted `BeginFact`/`RunFactCounted` call shapes).
2. **Protocol smoke:** build one small `tests-stark` project; run its
   runner by hand with and without `--progress`; assert the §3.2/§3.3 line
   shapes and that the no-flag transcript is byte-identical to legacy.
3. **Driver smoke:** `./stark test <small-project> --test-progress` — assert
   `[..s]` prefixes and live arrival (visually or by timestamping reads);
   add `--test-timeout 5` against a fixture with a deliberate
   `while willexit` hang — assert the kill, the explicit report, exit 1, and
   that the last `run <name>` line names the hanging fact.
4. **Stage parity (once stage1 exists):** run the same fixture under both
   drivers; diff transcripts with timing digits normalized.

Beware the stale-package sharp edge: run fixtures from package-free
directories, and `rm -rf` the fixture's `build/` if symbols like `BeginFact`
come up missing — a stale per-project stdlib package shadows fresh source.

## 8. Non-Goals (revisit only with a concrete consumer)

- **Machine-readable output** (TAP, JSON lines). The line protocol is already
  trivially parseable; pick a format only when a real consumer (CI dashboard,
  flaky-test tracker) exists, and add it as a separate runner flag so the
  human format stays stable.
- **Per-fact timeouts.** Whole-run deadline + start markers already isolates
  the hang; per-fact deadlines need a runner-side watchdog thread and clock.
- **Runner-side timing.** Driver-side prefixes are uniform and keep the
  runner clock-free.
- **Parallel fact execution.** Out of scope entirely (mega-main runner); if
  it ever lands, `run`/result lines need a worker tag — design then.

## 9. Task List for Future Agents

Completed (traceability):

- [x] T30.1 — `System.Testing.BeginFact` / `RunFactCounted` protocol
  functions (stage-shared).
- [x] T30.2 — stage0 runner generator: `--progress` argv parse (both
  collection and no-collection variants) + per-entry counted calls.
- [x] T30.3 — stage0 driver: `--test-progress` / `--test-timeout` flags,
  unconditional line streaming, elapsed prefixes, process-tree kill +
  explicit timeout report; generator tests updated; Internals doc section.

Completed 2026-07-03:

- [x] T30.4 — **Conformance fixture.**
  `tests/fixtures/test-progress/conformance` (passing fact, failing fact,
  platform-skipped fact, three-row theory, two collections, exactly ten
  entries) with byte-exact goldens under `goldens/` for §3.2 and §3.3 (the
  runner emits no timestamps, so both goldens are exact, not
  timing-normalized). Harness:
  `tests/compiler.IntegrationTests/TestProgressProtocolTests.cs` builds via
  `stark build` (test-kind projects build without running), executes the
  runner directly in both modes, and diffs against the goldens; a separate
  test asserts driver-level `[elapsed]`-prefixed streaming and another pins
  the no-flag output to the legacy golden. 4/4 green.
- [x] T30.5 — **Timeout regression test.**
  `tests/fixtures/test-progress/hang` (second fact spins behind an opaque
  `getppid()` ffi call so O3 cannot fold the loop);
  `TestTimeoutKillsHangingRunnerAndNamesInFlightFact` asserts exit 1, the
  explicit timeout report, `run HangsForever` as the last start marker, and
  no orphaned runner process (pgrep loop).
- [x] T30.6 — **`OwnedAscii` risk closure.** Resolved by the
  allocator-attribute wrong-code fix (§6); the fixture's tenth entry pins the
  double-digit `(10/10)` counter in the §3.3 golden.
- [x] T30.7 — **Stdlib audit for the stage1 driver.** Findings and gap items
  recorded in `TASKS.md` §4: pipe-backed spawn, chunked reads, monotonic
  clock, and no-hang wait all exist internally; missing are a public
  spawned-child handle, public chunk reads, a public monotonic clock, and
  process-group spawn/kill for tree-kill parity.
- [x] T30.8 — **Stage1 runner generator emission.**
  `selfhost/Compiler/TestRunner.stark` (`Compiler.TestRunner`): plan model
  (`TestRunnerPlan`/`TestRunnerEntryPlan`) plus `AppendGeneratedRunnerMain`,
  emitting the §3.1/§3.3 shapes for both the with-collections and
  standalone-argv variants. Validated byte-identical to stage0's generated
  main for the full conformance-fixture plan (scratchpad `trp_probe` diff).
  Attribute discovery/escaping stay with the future full `stark test` port.

- [x] T30.8b — **`tests-stark/selfhost.TestRunner` facts enabled** (2026-07-03):
  the gating host bug (imported struct holding a `List<T>` field broke
  consumer drop lowering) is fixed — package-image modules' field
  instantiations now materialize at type-check like source imports
  (TASKS.md §6 entry). 3/3 facts green; the package-backed
  `Compiler.TestRunner` emission byte-matches stage0's generated runner.

- [x] T30.9 — **Stage1 driver streaming + timeout component** (2026-07-03):
  `selfhost/Compiler/TestDriver.stark` (`Compiler.TestDriver`), built on the
  new `System.Process` surfaces (`ChildProcess` grouped `Spawn`, chunk
  reads, `PollChildPipes`, `TryWaitExit`/`WaitExit`, `KillTree`, public
  `MonotonicMilliseconds`). `RunTestRunner` passes `--progress` in the
  runner argv, forwards both streams line-by-line with `[N.Ns] ` monotonic
  prefixes, and at the deadline group-kills the child and appends the
  stage0-identical timeout report. Design deviation from the §5.2 sketch:
  single-threaded pipe-poll multiplexing (the stdlib's established
  `PollProcessPipesTimed` pattern) instead of thread-per-pipe + channel —
  same observable contract with no shared state. Validation (§7.4): probe
  ran the conformance-fixture runner through the component — legacy
  transcripts byte-identical to the §3.2 goldens, progress transcripts
  prefix-normalized-identical to the §3.3 goldens with every line prefixed,
  and the hang fixture under a 3-second deadline produced
  `run HangsForever` as the last marker, the exact §3.4 report text, and a
  clean group kill (no orphans). A portable driver fact
  (`DriverStreamsSpawnedRunnerWithPrefixes`) joined
  `tests-stark/selfhost.TestRunner` (4/4 green). Full `stark test` CLI
  wiring (project discovery, build orchestration) remains with the future
  stage1 CLI port, which composes these components.
- [x] T30.10 — **Docs closure** (2026-07-03): this status, the Internals
  "Test Runner Progress" section, and the TASKS.md stage-contract entry now
  point at `tests/fixtures/test-progress` as the normative artifact.
