# Unsupported Features

Stark is still pre-`2.0`. This page lists user-visible language, lowering, runtime, and standard-library areas that are not supported yet, plus the current diagnostic contract for those gaps.

For the active release checklist, see [Roadmap.md](./Roadmap.md). For pre-release notes, see [ReleaseNotes.md](./ReleaseNotes.md).

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

- Initializer gaps: unsupported variable initializer shapes; some object or array initializers still do not materialize a MIR value; variable initializers that cannot lower to a MIR operand.
- Assignment and expression gaps: assignment targets or values that cannot be resolved or coerced; expression statements that are neither assignments, rvalues, operands, nor the current ternary call-statement subset.
- Operator and type gaps: equality and ordered comparison still fall back outside the current scalar, text, same-kind fixed-array, same-kind slice, and scalarizable aggregate support.
- Name and call gaps: void-valued direct, member, and postfix calls cannot appear in value position.
- Place and update gaps: aggregate reads and writes still fall back when field or index paths cannot be resolved.
