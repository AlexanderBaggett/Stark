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

- Initializer gaps: unsupported variable initializer shapes; some object or array initializers still do not materialize a MIR value; variable initializers that cannot lower to a MIR operand.
- Assignment and expression gaps: assignment targets or values that cannot be resolved or coerced; conditional expressions outside the current ternary-only shape; expression statements that are neither assignments, rvalues, operands, nor the current ternary call-statement subset.
- Operator and type gaps: bitwise and shift operators still require integer operands; ordered comparison lowering now also supports same-kind fixed arrays and same-kind scalarizable `struct`/`record`/`enum` aggregates whose element types are ordered-comparable, and equality and inequality also support same-kind `ascii`/`unicode`, same-kind slices, and scalarizable aggregates over those leaf families.
- Name and call gaps: function names and function groups do not lower as first-class operands yet; void-valued direct, member, and postfix calls cannot appear in value position.
- Generic and package-image gaps: imported generic specialization now loads manifest-backed typed-interface and compiler-fact sections directly, and supported generic bodies can be seeded from typed template summaries during structured package-image loading instead of depending on bridge source text; that supported subset now stays declaration-only during structured package-image loading instead of being re-rendered into fake source bodies, and package publication now omits duplicated raw body text for those supported typed templates; the temporary synthetic-source bridge remains only as a compatibility fallback for templates that still lack sufficient typed summaries, but typed-interface sections now also carry published overload identity and generic-body availability directly so supported generic declaration loading and bridge body recovery no longer require source-surface function/type entries; published typed-template bodies now cover direct switch sections over literal/default/match-all/guarded labels, `if`/`while`/`for` control flow plus `break`/`continue`, ternary call statements, grouped local variable or constant declarators with supported initializers, grouped `for`-initializer local declarators, the full currently supported text postfix bracket family (`text[]`, `text[index]`, and `text[start, length]`), fixed-array local initializers, and local plus named-root field/index assignments, including compound assignments, nested generic type layouts now propagate through use-site planning, and typed-interface sections now carry structured import dependency surface so bridge reconstruction no longer depends on explicit source-surface imports for that path, but general published template bodies are not yet full typed template IR.
- Aggregate construction gaps: object initializers require resolved named fields; primary object creation only supports matched primary constructors; enum named constructors require named-field variants with complete payloads; enum positional constructors require exact arity; array initializers only lower for fixed arrays.
- Place and update gaps: aggregate reads and writes still fall back when field or index paths, or address materialization, cannot be resolved.
- Indexing gaps: slice and raw-pointer indexing require integer indices; indexing is only supported for fixed arrays, raw pointers, slices, `ascii`, and `unicode`; text postfix brackets currently support `text[]`, `text[index]`, and `text[start, length]`.
