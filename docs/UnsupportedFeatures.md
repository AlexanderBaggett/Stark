# Unsupported Features

Stark is still pre-`1.0`. This page lists user-visible language, lowering, runtime, and standard-library areas that are not supported yet, plus the current diagnostic contract for those gaps.

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

- Initializer gaps: unsupported variable initializer shapes; object or array initializers that do not materialize a MIR value; variable initializers that cannot lower to a MIR operand.
- Assignment and expression gaps: assignment targets or values that cannot be resolved or coerced; conditional expressions outside the current ternary-only shape; expression statements that are neither assignments, rvalues, nor operands.
- Operator and type gaps: dereference requires raw pointers; exponentiation requires a floating-point common type; integer-only operator chains reject non-integer common types; equality and comparison lowering is currently limited to integer, float, bool, and raw-pointer families.
- Name and call gaps: function names and function groups do not lower as first-class operands yet; generic function calls and generic type uses now type-check, deduplicate concrete instantiations, record instantiation triggers, assign cross-module ownership, and produce named monomorphization, specialization, and specialization-codegen-strategy plans with simple code-size, linkage, specialization-priority, and ambiguity diagnostics; source-backed generic instantiations can now materialize concrete HIR/MIR bodies with substituted local/object types, rewrite instantiated calls to those concrete symbols through MIR, SSA, and LLVM call sites, receive explicit ABI lowering, emit LLVM definitions when a materialized specialization body exists, and use `linkonce_odr`/COMDAT for imported source-backed specializations; package-image-backed public/export generic templates can now publish body sections plus minimal structured planning facts, including deferred nested-generic function and type-instantiation patterns, typed primary-constructor object-creation facts, typed object-creation target-type facts, typed object-initializer member facts, typed local declaration facts, typed explicit-conversion target facts, typed enum-constructor facts, typed tuple-enum-constructor call facts, typed unit-enum-case value facts, typed enum-pattern target facts, typed enum-pattern member facts, typed aggregate-pattern target facts, typed direct-call target facts, typed field-access facts, typed member-call target facts, a first typed template-body subset for simple linear helper bodies including literal returns, direct returns, object-creation returns, named enum-constructor returns including literal payload members, enum-call returns, enum-value returns, field-access returns, member-call returns, direct-call returns, and local-init-plus-return helpers, and ordinary import dependency surface in addition to `export import` re-exports, and published record types preserve primary-constructor shape across the package boundary, so consumers can emit owned concrete specializations, including nested generic callees and helper generic types discovered through those published patterns, without the original source file, with direct concrete calls and per-instantiation dedup inside the build, but general published template bodies are still source-text bridges rather than full typed template IR; void-valued direct, member, and postfix calls cannot appear in value position.
- Aggregate construction gaps: object initializers require resolved named fields; primary object creation only supports matched primary constructors; enum named constructors require named-field variants with complete payloads; enum positional constructors require exact arity; array initializers only lower for fixed arrays.
- Place and update gaps: aggregate reads and writes still fall back when field or index paths, or address materialization, cannot be resolved.
- Switch gaps: switch scrutinees must lower to operands; only the current direct switch subset lowers; text-switch partitioning still requires supported `ascii` or `unicode` view types and literal cases.
- Indexing gaps: dynamic fixed-array indexing currently requires a local fixed-array source and an integer index; slice and raw-pointer indexing require integer indices; indexing is only supported for fixed arrays, raw pointers, slices, `ascii`, and `unicode`; text slicing currently requires exactly two integer indices.
- Runtime-drop gaps: enum and aggregate drop helpers still mark MIR unsupported when tag, field, or comparison temporaries cannot be materialized.

## Text And Encoding Gaps

- Single-element text indexing is not implemented yet.
- General runtime `ascii` and `unicode` conversion is not implemented yet.

## Standard Library And Runtime Areas Still Incomplete

- Console input is not implemented yet.
- Richer text and file encoding conversion is still incomplete.
- Linux unicode console support is still transitional.
