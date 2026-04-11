# Unsupported Features

Stark is still pre-`2.0`. This page lists user-visible language, lowering, runtime, and standard-library areas that are not supported yet, plus the current diagnostic contract for those gaps.

For the active release checklist, see [Roadmap.md](../Internals/Roadmap.md). For pre-release notes, see [ReleaseNotes.md](./ReleaseNotes.md).

## Code Generation Contract

Inspection modes:

- `--check`
- `--emit-mir`
- `--emit-ssa`

These modes may still succeed when a program reaches an unsupported lowering path so the compiler can report diagnostics, logs, or intermediate artifacts.

Code generation modes:

- `--emit-llvm`
- `--emit-obj`
- `--emit-lib`
- `--emit-exe`

These modes fail with a stable `STK5000` diagnostic when direct code generation reaches an unsupported MIR lowering path. That keeps unsupported programs from silently drifting into declaration-only LLVM output.

## Current MIR Lowering Gaps

These are the constructs that still make it through parsing and type checking
and then stop in `lower-mir`. Surface restrictions that are rejected earlier do
not belong in this list.

There are no currently documented source-level MIR lowering gaps. Newly
discovered constructs that pass parsing and type checking but still stop in
`lower-mir` should be recorded here with a focused repro and regression test.
