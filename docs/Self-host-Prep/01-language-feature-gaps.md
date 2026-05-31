# Phase 1 - Language Feature Gap Analysis

Severity meanings:

- `blocker`: port cannot be kept faithful without this feature or a deliberate
  whole-port rewrite convention.
- `workaround-exists`: the compiler can be ported with more verbose Stark.
- `nice-to-have`: improves maintainability but does not block self-hosting.

Design states:

- `specified`: documented language contract exists.
- `partially implemented`: grammar/compiler support exists but is incomplete for
  self-hosting use.
- `unspecified`: no settled Stark surface or port convention found.

## Gap Table

| ID | Feature / Capability | Host Compiler Use | Severity | Design State | Audit Finding |
|---|---|---|---|---|---|
| L01 | Error-value propagation for `Result` / `Option` style code | Host uses `throw`, `try`, `catch`, nullable returns, and `InvalidOperationException` in `src/Compiler/CompilerPipeline.cs`, `src/Compiler/NativeToolchain.cs`, `src/Compiler/CompilerCli.cs`, parser helpers, package loading | blocker | partially implemented | Stark has no exceptions by design and has enum-shaped results in modules such as `System.IO.IOResult<T>` and `System.Memory.MemoryResult<T>`, but no shared `Result<T,E>` convention or `?`-style propagation. Ported code would otherwise expand every fallible call into manual `switch` plumbing. |
| L02 | Pattern binding in branch conditions (`if let`, `while let`) | C# relies on `is { } value`, nullable pattern checks, `switch` expressions, and guard-heavy branching throughout `TypeChecking.cs`, `SemanticValidation.cs`, package loaders | workaround-exists | unspecified | `Stark.g4` binds patterns only in `switch case`; `if`/`while` accept expressions. Manual `switch` blocks can replace this, but it will make result/error handling much noisier. |
| L03 | Iterator / foreach / collection traversal surface | Host uses `foreach`, LINQ, `IEnumerable<T>`, `yield return` in passes and helpers such as `CompilerPipeline.cs`, `ParameterMemoryContractFacts.cs`, `NativeToolchain.cs` | workaround-exists | partially implemented | Stark grammar has C-style `for` with explicit loop behavior and `while`, but no foreach or iterator protocol. Port can use indexed loops and collection APIs, but helper APIs must expose slice/count traversal. |
| L04 | Rich switch patterns: or-patterns, range patterns, list patterns, property patterns | C# source uses switch/pattern matching heavily in validators, lowering, text rendering, package codecs, and generated parser interaction | workaround-exists | partially implemented | Stark supports literal, discard, `var`, enum case, generic enum case, named-field enum, and aggregate patterns. No grammar evidence for `case A | B`, range patterns, property patterns, or list patterns. Expanded cases are viable. |
| L05 | Generic value parameters / const generics | Host models fixed arrays, bounded raw pointers, target/layout facts, and template bodies; stdlib and tests would benefit from generic fixed-size helpers | workaround-exists | unspecified | Stark generic parameters are type identifiers only. Fixed array lengths are expressions in type suffixes, but generic value parameters like `FixedArray<T, N>` are not in `Stark.g4`. |
| L06 | General operator/hash/equality doctrine surface for generic algorithms | Host relies on .NET string equality, `StringComparer`, generic hashing, numeric operators, comparison/sort helpers, dictionaries/sets throughout `TypeChecking.cs`, `ProjectCliDriver.cs`, package builders/loaders | blocker | partially implemented | Stark has `trait` and `doctrine`, but current stdlib docs limit `Dictionary<K,V>` keys to bool and integer key types. String-key dictionaries and generic hashing/equality must be specified or manually specialized. |
| L07 | Compiler invariant failure policy (`panic` / `trap` / abort) | Host throws for impossible states: missing artifacts in `CompilerPipeline.cs`, unsupported accepted bodies in LLVM/MIR paths, toolchain failures in `NativeToolchain.cs` | blocker | partially implemented | Language reference says recoverable errors are values and panic/assert/failure are trap-or-abort paths, but a compiler-facing invariant API and policy are not documented in stdlib. Port needs a uniform rule for internal compiler bugs. |
| L08 | Raw/multiline/interpolated string literal ergonomics for compiler text | Host builds LLVM IR, diagnostics, source snippets, JSON, and long test source strings with C# raw/interpolated strings and `StringBuilder` | workaround-exists | partially implemented | `Stark.g4` has `StringLiteral` and `$ StringLiteral`, but no raw/multiline literal syntax in grammar. Port can use builders and escaped strings, with a high maintenance cost for LLVM/test text. |
| L09 | General compile-time function evaluation / table generation | Host uses `CompileTimeExpressionEvaluator.cs`, `IntegerRangeStorageFacts.cs`, enum layout, SSA const lookup-table folding, text/path specializers | workaround-exists | partially implemented | Stark has `finite` and `law`, and endpoint arithmetic is compile-time evaluated, but no general `const fn` or finite-law CTFE surface for building compiler tables. |
| L10 | Async/build-driver concurrency replacement | `src/Compiler/ProjectCliDriver.cs` and `src/Compiler/CompilerCli.cs` expose `async Task<int>` and use async stream/process APIs | workaround-exists | unspecified | Stark has no `async`/`await` surface. A synchronous driver is viable; threaded build orchestration depends on stdlib threading gaps. |
| L11 | Nullability and optional-value conventions | Host nullable references (`string?`, `Type?`, `TryGet(... out T?)`) are pervasive in type/model queries | blocker | partially implemented | Safe Stark has no null references; raw `null` is only for raw/FFI. The port needs an `Option<T>` convention or equivalent enum patterns for all optional model state. |
| L12 | Partial/nested/generated type layout ergonomics | Host uses generated partial parser classes, nested parse contexts, nested helper classes, nested records, and partial lowering/emitter classes | workaround-exists | partially implemented | Stark supports modules and top-level data declarations, but no evidence of partial type declarations or nested named types. Port can flatten modules but generated parser strategy must account for this. |
| L13 | Alias/noalias proof carriers and wrong-alias diagnostics | Host tracks `ScopedNoAliasGroup`, memory contracts, root keys, `if disjoint(...)`, `unsafe assume disjoint(...)`, and validates call-site disjointness in `TypeChecking.cs` before LLVM emission | blocker | partially implemented | Source constructs exist and host records compile-time facts, but self-hosting must preserve the same artifact model and compile-time errors. Wrong alias-class use must remain a compiler diagnostic, not backend undefined behavior. |
| L14 | Parser generator/runtime strategy | Host parser is ANTLR-generated C# from `Stark.g4`, with runtime dependency and generated parse-context classes | blocker | unspecified | This is not a Stark source language feature, but it blocks expressing the compiler in Stark unless a Stark parser/runtime/generator strategy exists. Tracked in tooling as T01. |

## Features Verified Present Enough For Porting

| Feature | Evidence | Remaining Caveat |
|---|---|---|
| `struct`, `record`, `enum` | `Stark.g4`, `docs/Userfacing/LanguageReference.md` sections 8.1-8.4 | Porting records/classes still needs ownership/storage choices |
| Generic type parameters | `Stark.g4` `typeParameterList`, Language Reference 6.5 | Type-only generics, no const/value parameters |
| Function kinds | Grammar supports `fn`, `finite`, `law`, `finite law`; pipeline derives function effects | Port must preserve effect-refinement artifacts |
| Function pointers and closures | Language Reference 5.5-5.7 | Heap closure support and shared capture need careful stdlib/runtime review |
| Type aliases | Grammar `alias`, `TypeAliasSymbol`, `StarkTypeResolver` | Type aliases do not create distinct runtime alias classes |
| Borrow classes | `borrow`, `retborrow`, `storeborrow`, `frozen`, `shared`, `const` in grammar/docs | Port must avoid using C# shared references as a design crutch |
| Dynamic storage | Language Reference 6.4; stdlib uses `dynamic T` | Collection surfaces still need self-host compiler-grade APIs |
| Memory contracts | `where disjoint`, `where overlap`, `where same`, `if disjoint`, `unsafe assume disjoint` in grammar/docs/source | Alias proof carrier must be ported as a first-class artifact |
| Unsafe/FFI/asm | Grammar and stdlib platform modules | Self-host compiler should keep unsafe boundaries small and auditable |
| Loop behavior | `infinite`, `non-deterministic`, `willexit`, `independent` | Ported compiler loops need explicit annotations |

## Non-gaps With Large Translation Cost

| Topic | Why It Is Not A Gap | Porting Cost |
|---|---|---|
| No garbage collector | Stark intentionally uses ownership, move, drop, and borrow checking | Compiler IR graphs should use owned collections, arenas/handles, or explicit shared capability; direct C# object graph translation is risky |
| No exceptions | Stark intentionally uses values for recoverable errors | Requires broad error-value conventions and possible `trap` policy for invariants |
| Explicit storage classes | Stark locals require `stack`, `heap`, `register`, `static`, or future `arena` | Mechanical rewrite burden for every C# local |
| No trait objects | Stark traits/doctrines are compile-time contracts | Compiler abstractions should prefer closed-world generic functions/modules over runtime dispatch |
