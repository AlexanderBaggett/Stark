# Compiler Logging and Trace Design

Status: Core logging implemented; value tracing and trace artifacts deferred  
Scope: Milestone 6.5 core visibility, with mature-toolchain tracing deferred to v2.0

## Problem

Stark's compiler logs now support low-noise normal output, verbose output, stage
and symbol context, and structured event categories. They still do not fully
explain how individual values move through the compiler.

Today the main pain points are:

- value flow is still mostly invisible unless a developer inspects artifacts.
- it is hard to answer which function, source line, expression, or lowered value a log event refers to.
- some remaining `MarkUnsupported()` and similar early exits still need to be converted into first-class "gap" events.
- there is no structured on-disk trace artifact for post-mortem debugging.

The missing capability is not just "more logs". It is compiler observability: the ability to follow an entity through the pipeline and understand where it continued, changed shape, or stopped.

## Goals

The logging system must let a developer answer these questions:

- what function was being processed
- what source line produced this IR
- what value or expression was transformed
- whether it continued to the next stage
- whether it was rewritten, dropped, bypassed, or marked unsupported
- why a feature stopped lowering

For Milestone 6.5, the system should also:

- keep default console output concise
- support opt-in verbose mode
- support filtering by stage, symbol, source file, category, and gap-only views
- remain deterministic enough for regression tests

Longer term, Stark can add deeper trace modes and on-disk trace artifacts once the toolchain is closer to a mature debugger-facing experience.

## Non-Goals

This design is not trying to add:

- networked logging
- external telemetry backends
- profiler-quality performance tracing
- a requirement that every internal temporary receive a globally stable ID across compiler versions

## Design Summary

Stark should treat compiler observability as a phased system.

### Milestone 6.5

Milestone 6.5 should focus on the core auditability features needed to understand what the compiler still does not handle:

1. operational logs
2. gap events for incomplete features
3. light symbol and source-aware context

### v2.0

Once Stark needs mature debugging and post-mortem analysis features, it can extend the same event pipeline with:

1. value-trace logs
2. correlation IDs across lowering stages
3. on-disk trace artifacts

Both phases should share one structured event model so v1 work is not thrown away.

## Core Model

### Severity vs Verbosity

Severity answers "how serious is this?"

- `info`
- `warning`
- `error`

Verbosity answers "how much detail should be emitted?"

- `normal`
- `verbose`
- `trace` (v2.0)

This distinction is important. A value-flow event can be low-severity but high-detail. A warning can still appear in `normal` mode. Severity should not be misused as a noise-control mechanism.

### Categories

The event stream should use a small set of meaningful categories:

- `pipeline`
- `symbol`
- `decision`
- `gap`
- `value` (v2.0)
- `optimization`
- `import`
- `asm`

`gap` is the most important missing category. It is how the compiler says "this feature stopped here, and here is why."

### Outcomes

Events that describe compiler progress should use explicit outcomes:

- `continued`
- `rewritten`
- `dropped`
- `bypassed`
- `skipped`
- `stopped`
- `unsupported`
- `emitted`

For Milestone 6.5, the most important outcomes are `continued`, `stopped`, `unsupported`, `skipped`, and `bypassed`. Finer-grained value-flow outcomes can expand in v2.0.

### Standard Fields

Every compiler event should carry a common minimum shape:

- `sequence`
- `severity`
- `verbosity`
- `category`
- `eventId`
- `stage`
- `symbol`
- `location`
- `message`
- `outcome`
- `data`

Where relevant, events should also carry:

- `feature`
- `reason`
- `sourceText`

The following fields are explicitly post-1.0:

- `entityId`
- `entityKind`
- `inputEntityId`
- `outputEntityId`

The current `Data` bag remains useful, but the fields above should be first-class whenever possible instead of hidden in arbitrary key/value metadata.

## Scopes

The logger should support inherited scopes, similar to `ILogger.BeginScope(...)`.

A scope lets a compiler stage attach context once and avoid repeating it on every log call. For example:

- compilation scope
- pass scope
- symbol scope
- expression or entity scope

Example:

```text
compilation
  stage=lower-mir
  symbol=Demo.Run
  source=Demo.stark:4:1
```

Then inner events only need to describe the new information:

```text
Lowered index expression. [entity=expr@5:12 outcome=rewritten output=mir-temp:%7]
```

For Milestone 6.5, stage scope and symbol scope are enough. Deeper expression or value scopes can wait until v2.0.

## Event Types

### 1. Operational Events

Operational events answer "what is the compiler doing overall?"

Examples:

- pass started
- pass completed
- pass skipped
- pass crashed
- began lowering symbol
- finished lowering symbol

These events are useful, but they should not dominate `normal` output.

Recommended visibility:

- `normal`: warnings, errors, gap events, symbol begin/end, pass skips/stops/crashes
- `verbose`: add pass completion and major decisions
- `trace`: reserved for v2.0 value/entity flow

One operational event is deliberately promoted to `normal`: the slow-pass
heartbeat. When a pass takes longer than five seconds, pass completion logs a
warning-level event (`Pass 'X' took N.Ns`, category `pipeline`, kind
`pass-slow`, with the stage attached) at default visibility. Long compiles —
dependency sweeps, `stark build`, large-module type-check — would otherwise
sit silent for minutes; the heartbeat makes progress visible without turning
on `verbose`. Fast passes stay quiet, so ordinary compiles produce no extra
output.

### 2. Decision Events

Decision events explain why the compiler chose one path over another.

Examples:

- selected `asm(x86_64)` overload
- skipped pass because diagnostics already exist
- emitted declaration fallback instead of body
- selected imported manifest surface over source surface

These are high-value for debugging control flow through the pipeline.

### 3. Value Trace Events

Value trace events answer "what happened to this thing?"

Examples:

- expression typed
- expression lowered to HIR
- HIR value rewritten during MIR lowering
- MIR temporary materialized into SSA
- constant folded
- value dropped because it became dead

These are valuable, but they are not required for Milestone 6.5. They should be treated as v2.0 observability work.

### 4. Gap Events

Gap events explicitly represent incomplete implementation boundaries.

They should be emitted for:

- `MarkUnsupported()`
- early returns that stop lowering
- LLVM declaration fallbacks
- unsupported pattern shapes
- unsupported ABI or codegen combinations

Each gap event should include:

- function/symbol
- stage
- source span
- feature tag
- stop reason
- current entity if known
- whether lowering stopped for the symbol or only for a subtree

Example:

```text
Dynamic fixed-array index mutation is not lowered yet. [stage=lower-mir fn=Run src=Demo.stark:12:17 feature=fixed-array-dynamic-index-mutation outcome=unsupported]
```

This is much more actionable than "pass completed" or a generic fallback note, and gap events are the highest-priority observability feature for Milestone 6.5.

## Correlation and Identity

To follow entities through the pipeline perfectly, the system needs correlation IDs.

That is valuable, but it is a mature-toolchain feature. For Milestone 6.5, Stark can get most of the value it needs from:

- symbol name
- stage
- source span
- feature tag
- reason

Full correlation IDs should be deferred to v2.0.

### v2.0 Correlation Model

The full design should eventually support three levels:

### Symbol IDs

Stable within a compilation and preferably derived from the qualified symbol name.

Example:

```text
symbolId = fn:Demo.Run
```

### Source Entity IDs

Derived from syntax kind plus source span.

Example:

```text
entityId = expr:Demo.stark:12:17-12:29
```

This is usually enough to correlate source expressions across parsing, typing, and early lowering.

### Lowered Value IDs

Assigned when HIR, MIR, or SSA creates a new value or instruction that should be traceable.

Examples:

```text
hir-value:%23
mir-temp:%7
ssa-value:%12
```

Trace events should connect them explicitly:

```text
inputEntityId=expr:Demo.stark:12:17-12:29
outputEntityId=mir-temp:%7
outcome=rewritten
```

Global, forever-stable IDs are not required. Compilation-local IDs are enough for the future trace system.

## Sinks

The same event pipeline should support multiple sinks.

### Console Sink

Human-oriented. Compact by default.

`normal` example:

```text
[info] lower-mir Demo.Run at Demo.stark:4:1
[warn] gap lower-mir Demo.Run at Demo.stark:12:17 unsupported fixed-array dynamic index mutation; lowering stopped
```

`verbose` example:

```text
[info] lower-mir Demo.Run started
[info] selected source-backed import surface for Demo.Helpers
[info] lower-mir Demo.Run completed [2ms]
```

`trace` example:

```text
[trace] lower-mir Demo.Run expr:Demo.stark:12:17-12:29 values[index] -> mir-temp:%7 [outcome=rewritten]
```

The console sink should avoid current multi-line metadata dumps unless explicitly requested.

### Test Sink

Test output should use the same formatter and filtering rules as the console sink, but should be able to buffer or collapse per-test output so it is easier to read inside xUnit or VSTest hosts.

### File Sink

Opt-in file emission should write structured logs to disk.

Recommended format:

- JSON Lines (`.jsonl`) for machine-readable trace data

Recommended layout:

```text
.stark/logs/<compilation-id>/
  events.jsonl
  gaps.jsonl
  symbols/
    Demo.Run.jsonl
    Demo.Helpers.Add.jsonl
```

`events.jsonl` contains the full filtered stream.

`gaps.jsonl` contains only `gap` events, which makes incomplete features easy to audit.

Per-symbol files make it easier to inspect a single function without scrolling a giant global log.

This file-sink work is useful, but should be treated as v2.0 scope rather than Milestone 6.5 scope.

## Filtering

The system should support filtering independently of sink type.

Useful filters include:

- minimum severity
- verbosity mode
- category
- stage
- symbol
- source file
- gap-only

Entity-ID filtering belongs to the v2.0 trace system.

This should work the same way whether logs go to console, test output, or files.

## Formatting Strategy

The current formatting should change in two ways:

### 1. Message First

Human-facing output should start with the useful sentence, not metadata labels.

Bad:

```text
stage=emit-llvm operation=pass-complete symbol=<compilation>
message=Completed pass 'emit-llvm'.
```

Better:

```text
Completed emit-llvm. [stage=emit-llvm phase=codegen dur=0ms]
```

### 2. Metadata Only When It Adds Signal

Compilation-wide pass events do not need synthetic `1:1` locations or fake symbols in normal output.

By contrast, value and gap events absolutely do need:

- function
- source span
- entity
- outcome
- reason

## Proposed Default Behavior

Default compile output should emphasize signal:

- warnings and errors
- gap events
- symbol begin/end
- skipped, stopped, or crashed passes

Pass start/completion should move to `verbose` unless the event is unusual or important.

Detailed value flow belongs in `trace`, not `normal`, and `trace` is explicitly post-1.0.

## Example End-to-End Output

### Normal

```text
[info] lower-mir Demo.Run at Demo.stark:4:1
[warn] gap lower-mir Demo.Run at Demo.stark:12:17 dynamic fixed-array index mutation is not lowered yet; lowering stopped
[info] emit-llvm skipped [prior-errors]
```

### Verbose

```text
[info] lower-mir Demo.Run started
[warn] gap lower-mir Demo.Run at Demo.stark:12:17 dynamic fixed-array index mutation is not lowered yet; lowering stopped
[info] emit-llvm skipped [prior-errors]
```

### Trace (v2.0)

```text
[trace] type-check Demo.Run expr:Demo.stark:12:17-12:29 values[index] typed as i32
[trace] lower-hir Demo.Run expr:Demo.stark:12:17-12:29 -> hir-value:%23 [outcome=continued]
[trace] lower-mir Demo.Run hir-value:%23 -> mir-temp:%7 [outcome=rewritten]
[warn] gap lower-mir Demo.Run expr:Demo.stark:12:17-12:29 feature=fixed-array-dynamic-index-mutation reason=requires-local-fixed-array-source outcome=unsupported
[trace] lower-mir Demo.Run expr:Demo.stark:12:17-12:29 [outcome=stopped]
```

### JSONL (v2.0)

```json
{"sequence":41,"severity":"warning","verbosity":"normal","category":"gap","eventId":"unsupported-lowering","stage":"lower-mir","symbol":"Demo.Run","location":"Demo.stark:12:17","entityId":"expr:Demo.stark:12:17-12:29","entityKind":"expression","feature":"fixed-array-dynamic-index-mutation","reason":"requires-local-fixed-array-source","outcome":"unsupported","message":"Dynamic fixed-array index mutation is not lowered yet."}
```

## Rollout Plan

The recommended implementation order is:

### Milestone 6.5

1. separate verbosity from severity
2. add stage and symbol scopes
3. introduce first-class `gap`, `symbol`, and `decision` events
4. convert `MarkUnsupported()` and other early exits into gap events
5. tighten console and test-host formatting for `normal` and `verbose`
6. add regression tests for filtering, formatting, and gap coverage

### v2.0

1. add first-class `value` events
2. add correlation IDs for symbols, source entities, and lowered values
3. add opt-in JSONL file sinks
4. add per-symbol trace files and gap-only artifact views
5. add regression tests for tracing and emitted trace artifacts

## Recommendation

Stark should keep the current structured logging foundation, but phase the work.

Milestone 6.5 should deliver:

- low-noise default output
- opt-in verbose mode
- explicit gap events for incomplete features
- stage, symbol, and source-aware context

v2.0 can then extend that base with:

- full value tracing
- entity correlation across pipeline stages
- optional on-disk trace artifacts

That gives Stark both of the things it currently lacks:

- readable day-to-day logs
- actionable visibility into what lowering does not handle yet
