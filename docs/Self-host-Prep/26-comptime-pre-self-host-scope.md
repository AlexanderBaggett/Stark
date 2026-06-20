# Comptime Pre-Self-Host Scope

Status: locked baseline.

This document defines the `comptime` capability that remains relevant before
self-hosting. The goal is to preserve what already works, keep the language
internally consistent, and stop broad `comptime` expansion from blocking the
compiler port.

## Decision

Pre-self-host `comptime` is a narrow baseline:

- keep the currently implemented syntax for `comptime expr` and value-position
  `comptime { ... }` blocks
- keep typed range-integer `comptime` generic parameters
- keep deterministic CTFE forms that are already implemented and covered by
  tests
- keep runtime-boundary diagnostics and backend erasure for compiler-known
  compile-time-only facts
- do not add new broad `System.Compiler` fact families as a self-hosting
  blocker
- do not require full evaluator parity or complete cross-package preservation
  for arbitrary future CTFE forms before bootstrap

Bug fixes and regressions are still in scope. New feature work is not, unless a
ported compiler module proves that the missing behavior is required before the
first self-hosted compiler can build.

## Included Capability

The pre-self-host baseline includes the current host implementation:

- scalar constant folding and typed constant materialization
- fixed-array, named aggregate, and enum aggregate constants
- `sizeof` / `alignof` layout queries over concrete layouts
- typed integer `comptime` generics for fixed-array lengths, table shapes,
  fixed-capacity helpers, monomorphization identity, and package/source
  identity
- deterministic local mutation inside accepted CTFE bodies
- bounded `willexit` loops and explicit traversal loops already accepted by the
  evaluator
- `if`, `switch`, pattern conditions, pattern switches, guards, `return`,
  `break`, and `continue` within the already implemented deterministic subset
- declared `finite` / `law` calls already supported by the host evaluator,
  including supported static calls, receiver calls, chained receiver calls, and
  trait-default receiver calls
- CTFE `try` propagation over role-marked result/option/status-shaped enums
- currently implemented `System.Compiler` facts, treated as compiler-known
  compile-time-only facts rather than a standard-library module
- diagnostics for unsupported compile-time execution
- erasure before MIR/LLVM lowering

## Excluded Before Self-Host

These are not pre-self-host blockers:

- completing broad Zig-like comptime as a general language runtime
- adding compile-time file I/O, process execution, clocks, randomness, or
  environment access
- CTFE over arbitrary stdlib containers
- expanding `System.Compiler` to cover every possible declaration query
- making `System.Compiler` a real stdlib module
- perfecting source-free imported-template CTFE for future forms that the
  compiler port does not actually use
- guaranteeing full parity for arbitrary accepted runtime Stark syntax inside
  CTFE

## Maintenance Rule

Before self-hosting, `comptime` work should be limited to:

- preserving current tests
- fixing regressions in currently implemented behavior
- adding portable tests for already-realized semantics when coverage is missing
- adding a missing CTFE behavior only when the active compiler port proves it is
  necessary and cannot be better expressed as ordinary runtime compiler code

Anything else belongs in
[27-comptime-post-self-host-scope.md](27-comptime-post-self-host-scope.md).
