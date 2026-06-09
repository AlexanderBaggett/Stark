# CTFE Self-Host Compiler Audit

Status: complete for the current C# host compiler snapshot; superseded for
scheduling by the pre/post self-host scope split.

This audit records what the self-hosted Stark compiler must be able to do at
compile time. It is deliberately scoped to compiler-port needs found in the host
implementation, not to a general compile-time runtime.

## Host Sources Read

| Area | Host files |
| --- | --- |
| Expression CTFE | `src/Compiler/CompileTimeExpressionEvaluator.cs` |
| Function/block CTFE | `src/Compiler/CompileTimeFunctionEvaluator.cs` |
| MIR and imported-template CTFE | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.CompileTimeEvaluator.cs`, `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.ImportedTemplateLowerer.cs` |
| Type-check integration | `src/Compiler/TypeChecking.cs` |
| Structural facts | `src/Compiler/CompileTimeStructuralFacts.cs` |
| Integer, enum, ABI/layout facts | `src/Compiler/IntegerRangeStorageFacts.cs`, `src/Compiler/EnumLayoutBuilder.cs`, `src/Compiler/StarkCDataModelFacts.cs`, `src/Compiler/AbiLowering.cs`, `src/Compiler/CAbiAggregateClassifier.cs` |
| Package-image preservation | `src/Compiler/PackageImage/Builder/GenericTemplateSectionBuilder.cs`, `src/Compiler/PackageImage/Loader/CompilerFactsSectionLoader.cs`, `src/Compiler/PackageImage/Bridge/PackageImageSourceBridge.cs` |
| Current tests | `tests/compiler.FeatureTests/ComptimeFeatureTests.cs`, `tests/compiler.Tests/PackageImageArchitectureTests.cs`, `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.CompileTimeEvaluator.cs` |

## Required CTFE Use Cases

| Use case | Host evidence | Required Stark capability | Status |
| --- | --- | --- | --- |
| Scalar constant folding | `CompileTimeExpressionEvaluator` handles literals, arithmetic, comparisons, casts/conversions, text constants, null raw pointers, and constant identifiers. | Evaluate deterministic scalar expressions during type checking and MIR lowering. | Covered for current compiler-port needs. |
| Range and storage facts | `IntegerRangeStorageFacts` and type-check sites evaluate integer widths/ranges, fixed-array lengths, raw-pointer counts, and `comptime` generic values. | Range-typed integer CTFE up to Stark's supported widths, with too-large values diagnosed instead of widened to public BigInt. | Covered. |
| Bounded local table construction | `CompileTimeFunctionEvaluator` and MIR CTFE support local variables, mutable local assignment, fixed-array initializers, element writes, field writes, and `willexit` loop budgets. | Build generated tables and compact constants without runtime work. | Covered for fixed arrays and named/enum aggregates. Dynamic builders are not a verified CTFE requirement. |
| Compile-time control flow | Function evaluators support `return`, `break`, `continue`, `if`, `switch`, pattern-condition `if`, `while willexit`, classic `for willexit`, and traversal `for willexit`. | Branch over compile-time values and explicit program-structure facts. | Covered. |
| Pattern execution | Function evaluators support literals, ranges, discard/capture, fixed-array list patterns, aggregate positional/property patterns, enum unit/tuple/named-field patterns, and guards. | Let compiler helper code use the same switch vocabulary at compile time as at runtime. | Covered for the current pattern subset. |
| Aggregate constants | Evaluators and package tests cover fixed arrays, structs/records, primary constructors, object initializers, enum values, enum constructors, field projection, and zero initialization. | Materialize constants directly before runtime lowering. | Covered. |
| Layout queries | Type checking and MIR CTFE use `sizeof` / `alignof` and concrete layout facts through `ConcreteTypeLayoutHelper`. | Query concrete runtime layout at compile time for ABI, package, and backend decisions. | Covered for concrete layouts. |
| Result-like propagation | CTFE has `try` propagation over role-marked two-variant enums and no-payload status forms. | Reuse the normal Stark error-flow surface in compile-time compiler helpers. | Covered. |
| Finite/law calls | Type-check and MIR evaluators call declared `finite` / `law` functions and methods, including static calls, receiver calls, chained receiver calls, generic substitution, and trait-default receiver fallback. | Reuse ordinary pure compiler helpers at compile time without trait objects. | Covered for the supported deterministic subset. |
| Structural facts | `CompileTimeStructuralFacts` provides explicit `System.Compiler` facts for type categories, layout, field/enum metadata, callable metadata including actual return/parameter type-argument facts, traits/doctrines, associated types, `comptime` generics, FFI ABI, C aliases, qualifiers, memory contracts, and thread-safety laws. | Let generic compiler helpers branch over program structure without runtime reflection. | Broadly covered; remaining fact additions should be driven by real port queries. |
| Cross-package generic CTFE | Package builders publish typed template bodies and structural fact expressions; imported-template lowering folds them after type and `comptime` value substitution. Source-free imported direct and receiver-member CTFE calls execute preserved typed statement bodies with local state, object/array initializer constants, scalar text interpolation and constant concatenation, control flow, pattern conditions, pattern switches, guards, and callee-specific typed metadata instead of relying on stale source text. | Imported generic compiler helpers must behave like local generic helpers. | Covered for the current typed-template surface; preserve future required forms as they appear. |
| Runtime erasure | Type checking rejects structural fact calls outside `comptime`; MIR/codegen tests assert no CTFE calls leak into runtime lowering. | Compile-time-only facts must have zero runtime cost. | Covered by targeted regressions. |

## Audit Findings

| Finding | Meaning for self-hosting |
| --- | --- |
| CTFE is a deterministic subset of ordinary Stark, not a separate language. | The self-hosted compiler can use the same finite/law helper style for generated constants, tables, layout branches, and generic specializations. |
| Program-structure branching is explicit through `System.Compiler` facts. | Stark does not need runtime reflection or hidden metadata dispatch for self-hosting. New facts should be added only when the compiler port has a concrete query. |
| Host package-image generation is ordinary compiler runtime work. | Module/package/top-level declaration enumeration does not need to become CTFE unless a future compiler helper intentionally moves that work into `comptime`. |
| The current implementation already covers more than the narrow pre-self-host baseline. | Preserve it as regression-protected host behavior, but do not expand it as a bootstrap blocker. |
| The main risk is evaluator drift. | Before self-hosting, fix drift only when it breaks the frozen baseline or an active compiler-port requirement. Broad parity is post-self-host work. |
| The second risk is package-image preservation drift. | Before self-hosting, preserve currently tested cases. New broad preservation work belongs to post-self-host unless the port proves it is required. |

## Not Required For Initial Self-Hosting CTFE

| Non-requirement | Reason |
| --- | --- |
| Compile-time file I/O, process spawning, environment access, clocks, or random values | The compiler performs these as ordinary runtime compiler operations. Making them CTFE would reduce determinism and is not required by the host design. |
| Public arbitrary-precision BigInt | Stark has wide fixed integers such as `i1024` / `u1024`; too-large literals should be diagnosed. |
| Runtime reflection | The intended model is explicit compile-time-only `System.Compiler` facts erased before backend lowering. |
| Hidden trait objects or hidden dispatch | Reuse stays compile-time-only by default; explicit runtime dispatch belongs to explicit ops tables. |
| CTFE over arbitrary stdlib containers | The host needs fixed arrays, aggregates, scalar/text constants, and structural facts. Dynamic collections remain ordinary runtime compiler data structures unless a concrete compile-time use case appears. |

## Scheduling Outcome After Scope Split

The audit no longer implies that the broad `comptime` feature must close before
self-hosting. The schedule is:

| Work area | Schedule | Notes |
| --- | --- | --- |
| Frozen pre-self-host baseline | before self-host | Preserve the currently implemented CTFE syntax, typed integer `comptime` generics, deterministic constants/tables/layout queries, supported finite/law calls, existing structural facts, diagnostics, and erasure. |
| Evaluator parity closure | post-self-host by default | Close broad parity in the Stark compiler architecture unless the active compiler port proves a specific pre-self-host requirement. |
| Port-driven `System.Compiler` fact closure | post-self-host by default | Add new fact families only when real compiler code needs them; broad structural reflection remains rejected. |
| Package-image/source-bridge preservation closure | post-self-host by default | Preserve current tested cases before bootstrap; complete broad preservation later. |
| Final broad-comptime closure pass | post-self-host | Re-run this audit once the Stark compiler source exists and decide which broad `comptime` pieces are still worth implementing. |

## Completion Criteria For Pre-Self-Host Comptime

- The currently implemented CTFE baseline remains regression-tested.
- Unsupported compile-time execution reports a compile-time diagnostic.
- Compile-time-only facts and calls remain rejected at runtime and erased before
  runtime lowering.
- Any new pre-self-host CTFE work is justified by a concrete compiler-port
  blocker and documented in the roadmap at the same task granularity as other
  blockers.
