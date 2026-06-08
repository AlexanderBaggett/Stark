# Stark Self-Hosting Roadmap

This file is the short task tracker for self-hosting prep. Keep it as an
at-a-glance checklist. Put rationale, design notes, audits, and detailed gap
tables in the companion documents in this folder.

Legend: `[ ]` not done, `[~]` partially done, `[x]` done.

Roadmap checkboxes track high-level progress. When a roadmap item links to a
companion document's checklist or `Work Items` section, mark the roadmap item
done only when that linked low-level list is complete. Use `[~]` here when some
linked low-level work has landed but the detailed list still has open items.

## Context Documents

- Host inventory and gap audit: [00-host-compiler-inventory.md](00-host-compiler-inventory.md), [01-language-feature-gaps.md](01-language-feature-gaps.md), [02-stdlib-gaps.md](02-stdlib-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md), [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md)
- Port plan and milestones: [05-port-checklist.md](05-port-checklist.md), [06-roadmap.md](06-roadmap.md), [07-open-questions.md](07-open-questions.md)
- Feature and architecture drafts: [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md), [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md), [10-traits-and-dynamic-dispatch.md](10-traits-and-dynamic-dispatch.md), [11-error-propagation.md](11-error-propagation.md), [12-atomics.md](12-atomics.md), [13-comptime.md](13-comptime.md), [14-thread-safety-laws.md](14-thread-safety-laws.md), [15-ffi-abi.md](15-ffi-abi.md), [16-ffi-c-types.md](16-ffi-c-types.md), [17-ffi-struct-layout.md](17-ffi-struct-layout.md), [18-ffi-c-strings.md](18-ffi-c-strings.md), [19-generic-collections-and-interning.md](19-generic-collections-and-interning.md), [20-package-image-format.md](20-package-image-format.md), [21-system-toml.md](21-system-toml.md), [22-threading-coordination.md](22-threading-coordination.md), [23-libllvm-integration.md](23-libllvm-integration.md), [24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md), [25-build-artifact-layout.md](25-build-artifact-layout.md)
- Packaging and release planning: [SelfHostingRoadmap.md](SelfHostingRoadmap.md), [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md)

## Detailed Task Lists

- Compiler and test port rows: [05-port-checklist.md](05-port-checklist.md)
- Test infrastructure capabilities: [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md)
- Language, stdlib, and tooling gap tables: [01-language-feature-gaps.md](01-language-feature-gaps.md), [02-stdlib-gaps.md](02-stdlib-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md)
- Traits and dynamic dispatch implementation tables: [10-traits-and-dynamic-dispatch.md](10-traits-and-dynamic-dispatch.md)
- Error propagation implementation tables: [11-error-propagation.md](11-error-propagation.md)
- FFI ABI implementation work items: [15-ffi-abi.md](15-ffi-abi.md#9-implementation-work-items)
- FFI C primitive alias work items: [16-ffi-c-types.md](16-ffi-c-types.md#10-work-items)
- FFI struct layout implementation work items: [17-ffi-struct-layout.md](17-ffi-struct-layout.md#11-implementation-work-items)
- FFI C string work items: [18-ffi-c-strings.md](18-ffi-c-strings.md#11-work-items)
- Generic collections and interning work items: [19-generic-collections-and-interning.md](19-generic-collections-and-interning.md#8-work-items)
- Thread-safety law work items: [14-thread-safety-laws.md](14-thread-safety-laws.md#15-work-breakdown-ts)
- Package image format work items: [20-package-image-format.md](20-package-image-format.md#8-work-items)
- `System.Toml` work items: [21-system-toml.md](21-system-toml.md#7-work-items)
- Threading coordination work items: [22-threading-coordination.md](22-threading-coordination.md#8-work-items)
- libLLVM integration work items: [23-libllvm-integration.md](23-libllvm-integration.md#8-work-items)
- IR memory and fact model work items: [24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md#9-work-items)
- `.stark/build` artifact layout work items: [25-build-artifact-layout.md](25-build-artifact-layout.md#7-work-items)
- FFI syntax design checklist: [stark-ffi-syntax-checklist.md](stark-ffi-syntax-checklist.md)
- Release packaging checklist: [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md)

## Book Updates

Book-facing updates are scheduled as one documentation batch, separate from
feature implementation. Related `LanguageReference.md`, `System.*` reference,
SKILL, and FFI checklist edits may land in the same batch when they keep the
user-facing examples consistent.

- [ ] Update the book and user-facing FFI docs for explicit ABI spelling and ABI-bearing function pointer types. Source work item: [15-ffi-abi.md](15-ffi-abi.md#10-book-and-reference-work).
- [ ] Update the book and user-facing FFI docs for `System.C` C primitive aliases, target-mapped widths, `c_char` signedness, and `c_void`. Source work items: [16-ffi-c-types.md](16-ffi-c-types.md#10-work-items).
- [ ] Update the book and user-facing FFI docs for `[StructLayout(C)]`, `Pack(N)`, `Align(N)`, explicit field offsets, and packed-field safety. Source work items: [17-ffi-struct-layout.md](17-ffi-struct-layout.md#12-book-and-reference-work).
- [ ] Update the book and user-facing FFI docs for `System.C.CStr`, `OwnedCStr`, `CCharBuffer`, explicit C-string conversions, and `%s` varargs rules. Source work items: [18-ffi-c-strings.md](18-ffi-c-strings.md#11-work-items).
- [ ] Update the Stark language skill references and FFI syntax checklist for the batched FFI surface. Source checklist: [stark-ffi-syntax-checklist.md](stark-ffi-syntax-checklist.md).
- [ ] Update the book and user-facing language reference for `raw"..."`, `raw"""..."""`, and `$raw` text literals. Source item: L08 in [01-language-feature-gaps.md](01-language-feature-gaps.md).
- [ ] Update the book and user-facing stdlib docs for generic collection contracts, text key equality/hash/order semantics, `HashSet<T>`, ordered collections, and explicit interning guidance. Source work items: [19-generic-collections-and-interning.md](19-generic-collections-and-interning.md#9-book-and-reference-work).
- [ ] Update user-facing project/build docs once binary package-image and `stark inspect-pkg` spelling are finalized. Source work items: [20-package-image-format.md](20-package-image-format.md#9-documentation-work).
- [ ] Update user-facing project/build docs for the formal `.stark/build/<profile>/<target-triple>/<stage>/` layout after command spelling and artifact names are implemented. Source work items: [25-build-artifact-layout.md](25-build-artifact-layout.md#7-work-items).
- [ ] Update the standard library reference and project/build docs for `System.Toml` once the public API is finalized. Source work items: [21-system-toml.md](21-system-toml.md#8-documentation-work).
- [ ] Update the book and user-facing docs for `Transferable` / `Shareable` thread-safety laws when that design lands. Source spec: [14-thread-safety-laws.md](14-thread-safety-laws.md).
- [ ] Update the standard library reference and book threading chapter for captured thread starts, `Synchronized<T>`, `Locked<T>`, and channels after the API lands. Source work items: [22-threading-coordination.md](22-threading-coordination.md#9-documentation-work).
- [ ] Update build/toolchain, compiler-internals, and FFI docs for libLLVM-primary backend integration after the API lands. Source work items: [23-libllvm-integration.md](23-libllvm-integration.md#9-documentation-work).

## Current Priorities

- [ ] Finish the Stark-native test infrastructure so tests can be ported first. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Port the existing host compiler tests to Stark while still running them against the C# host compiler. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Close the remaining language, stdlib, and tooling blockers that prevent the compiler from being written in Stark. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [02-stdlib-gaps.md](02-stdlib-gaps.md), and [03-tooling-gaps.md](03-tooling-gaps.md).

## Completed Prep

- [x] Inventory the host compiler and document the major subsystems, dependencies, file formats, and external tools. See [00-host-compiler-inventory.md](00-host-compiler-inventory.md).
- [x] Audit language, stdlib, tooling, and test-infrastructure gaps. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [02-stdlib-gaps.md](02-stdlib-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md), and [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [x] Draft the per-file compiler and test port checklist. See [05-port-checklist.md](05-port-checklist.md).
- [x] Draft the milestone roadmap and open-decision list. See [06-roadmap.md](06-roadmap.md) and [07-open-questions.md](07-open-questions.md).
- [x] Draft the self-hosted compiler architecture direction. See [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [x] Implement and document error-value propagation with `try`, `[Ok]`/`[Err]` roles, and `from` funnels. See [11-error-propagation.md](11-error-propagation.md).
- [x] Implement pattern conditions for `if` and `while` using `is` patterns. See [01-language-feature-gaps.md](01-language-feature-gaps.md).
- [x] Close the invariant-failure policy around unrepresentable invalid states, switch exhaustiveness, and definite return. See [01-language-feature-gaps.md](01-language-feature-gaps.md) and [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [x] Implement the current trait/constrained-generic/dynamic-dispatch slice needed for reuse and explicit runtime dispatch. See [10-traits-and-dynamic-dispatch.md](10-traits-and-dynamic-dispatch.md).
- [x] Implement atomics as the first concrete shared-state primitive. See [12-atomics.md](12-atomics.md).
- [x] Implement the declaration surface for `Transferable` / `Shareable` thread-safety laws: law predicates, `[Grant]` / `[Deny]` attributes, syntax/type symbols, and package/source preservation. Law computation and enforcement remain tracked in [14-thread-safety-laws.md](14-thread-safety-laws.md#15-work-breakdown-ts).
- [x] Implement compile-time law computation for `Transferable` / `Shareable`: cached structural derivation, generic propagation, field/type grant/deny resolution, conflict diagnostics, unsafe-reference defaults, intrinsic atomic grants, conditional grants, and direct/member call-site enforcement for callable `where` law predicates. Thread-boundary consumer enforcement remains tracked in [14-thread-safety-laws.md](14-thread-safety-laws.md#15-work-breakdown-ts).
- [x] Draft the explicit FFI ABI syntax spec. See [15-ffi-abi.md](15-ffi-abi.md).
- [x] Draft the target-mapped C primitive alias spec for FFI. See [16-ffi-c-types.md](16-ffi-c-types.md).
- [x] Draft the C-style struct layout, packing, and alignment spec for FFI. See [17-ffi-struct-layout.md](17-ffi-struct-layout.md).
- [x] Draft the null-terminated C string interop spec for `System.C`. See [18-ffi-c-strings.md](18-ffi-c-strings.md).
- [x] Decide the minimal threading coordination scope for future parallel build/test work: captured thread payloads, easy guarded shared state, and channels. See [22-threading-coordination.md](22-threading-coordination.md).
- [x] Decide LLVM integration: use libLLVM through direct LLVM C API module construction, with textual LLVM retained only as printed debug/artifact inspection output. See [23-libllvm-integration.md](23-libllvm-integration.md).
- [x] Decide bootstrap policy: use the existing C# host compiler as Stage0 until the Stark compiler can build itself; do not add a separate blessed snapshot compiler artifact. See [07-open-questions.md](07-open-questions.md).
- [x] Decide editor/tooling blocking status: track syntax/editor updates, but do not block bootstrap on full editor parity. See [07-open-questions.md](07-open-questions.md) and [03-tooling-gaps.md](03-tooling-gaps.md).
- [x] Decide the compiler IR memory model: arena/table storage with typed handles, first-class extensible fact tables, explicit fact lowering policies, package-image durable facts, and phase-boundary verification. See [24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md).
- [x] Decide the `.stark/build` layout: use `.stark/build/<profile>/<target-triple>/<stage>/` with stable artifact subdirectories. See [25-build-artifact-layout.md](25-build-artifact-layout.md).

## Test Infrastructure

- [x] Decide test discovery for Stark tests: generate an explicit `main` runner from `[Fact]` metadata, with no runtime reflection. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md) and [07-open-questions.md](07-open-questions.md).
- [ ] Implement generated Stark test runners, including fact enumeration, selected-test filters, and stable result reporting. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md), [03-tooling-gaps.md](03-tooling-gaps.md), and [05-port-checklist.md](05-port-checklist.md).
- [ ] Add rich assertions for diagnostics, collections, ranges, null/option-like values, and type checks. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Add golden-file and snapshot helpers for diagnostics, LLVM, MIR, SSA, and package text. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Add temp directory, temp file, cleanup, and fixture-editing helpers. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md) and [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add process execution with stdout, stderr, exit-code, argv, environment, and working-directory capture. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md) and [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add a host-compiler target mode so Stark tests can run against the current C# compiler. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Implement fast compiler artifact and diagnostic inspection for deep pipeline tests: typed in-process compiler test API first, persistent/batched compiler runner with structured results for host and cross-stage tests, and selective full artifact export for targeted golden/stage/debug tests. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md), [03-tooling-gaps.md](03-tooling-gaps.md), and [07-open-questions.md](07-open-questions.md).
- [ ] Add parameterized tests, platform gates, serial-test groups, and regression harness support. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Expand `System.Testing` to support the ported host test suite. See [02-stdlib-gaps.md](02-stdlib-gaps.md) and [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).

## Port The Tests First

- [ ] use the existing SKILL.md for all stark related tasks.
- [ ] Port shared test helpers and fixtures. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port feature tests that primarily check LLVM text output. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port parser, diagnostic, type checking, ownership, MIR, SSA, LLVM, and package tests. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port pipeline artifact tests after artifact inspection exists. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md) and [05-port-checklist.md](05-port-checklist.md).
- [ ] Port standard-library tests in compile-only slices first, then runtime slices. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port CLI, project, package, native-toolchain, and integration tests once process/tooling support exists. See [03-tooling-gaps.md](03-tooling-gaps.md) and [05-port-checklist.md](05-port-checklist.md).
- [ ] Add Stark test-runner project files to replace the current xUnit runner configuration. See [03-tooling-gaps.md](03-tooling-gaps.md), [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md), and [05-port-checklist.md](05-port-checklist.md).

## Language Work

- [x] Finish the compile-time-only reuse surface: default method bodies, associated types, and canonical `Eq`, `Hash`, `Ord`, and `Format` contracts. Default members, associated type requirements/defaults, package-image preservation, explicit static `Dictionary<K,V>` key `Hash`/`Equals`, and the stdlib contract names have landed. See [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md) and [10-traits-and-dynamic-dispatch.md](10-traits-and-dynamic-dispatch.md).
- [x] Finish cross-module trait conformance checks and generic-dispatch coverage. See [10-traits-and-dynamic-dispatch.md](10-traits-and-dynamic-dispatch.md).
- [x] Decide how optional values replace host nullability and nullable C# APIs: use `System.Option<T>` with `[Ok] Some(T)` / `[Err] None` as the blessed porting convention. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [07-open-questions.md](07-open-questions.md), and [11-error-propagation.md](11-error-propagation.md).
- [x] Decide alias/noalias proof model: use explicit compile-time-only proof carriers for APIs that need alias facts, erase those carriers before codegen, reject wrong proof use as a compile-time diagnostic, and allow external facts only through narrow `unsafe assume disjoint(...)` construction with explicit root checks. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [07-open-questions.md](07-open-questions.md), and [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [x] Implement alias/noalias proof carriers, package-image preservation, root validation, lowering validation, and diagnostics. Declaration-level memory contracts are preserved in package images; runtime `if disjoint(...)` and `assume disjoint(...)` facts lower into typed SSA proof carriers; malformed carriers/root keys fail SSA validation before LLVM emission. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [05-port-checklist.md](05-port-checklist.md), and [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [x] Decide the parser strategy for the self-hosted compiler: keep `Stark.g4` as the canonical grammar reference, but implement a handwritten self-hosted parser instead of porting ANTLR runtime/generated parsers. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md), [07-open-questions.md](07-open-questions.md), and [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [x] Decide `comptime` scope: CTFE plus broad compile-time branching over explicit program-structure facts, with compile-time-only structural facts erased before backend lowering. See [13-comptime.md](13-comptime.md).
- [~] Implement the self-hosting `comptime` capability as one high-level language feature: expression/block CTFE, typed `comptime` generics, deterministic local mutation, bounded CTFE loops/traversal, aggregate constants, layout queries, switch/pattern execution, explicit conversion/cast evaluation, CTFE `try` propagation over role-marked enums, generic CTFE substitution, symbolic `comptime` value preservation through nested finite/law calls until specialization, declared `finite`/`law` function and method calls including chained receiver calls and trait-default receiver calls, cross-package/package-image preservation for CTFE `try` and typed-template structural facts, and explicit `System.Compiler` structural facts, including typed thread-safety law attribute condition type predicate/metadata facts for `[Grant]` / `[Deny]` on types and fields, implemented-trait type predicate/metadata facts, declaration and actual `comptime` generic argument type predicate/metadata facts, ordinary actual type argument predicate/metadata facts, method module identity facts, method generic trait-bound type predicate/metadata facts, method `where` law predicate type predicate/metadata facts, C source alias identity facts including closure return/parameter type aliases, named type module identity facts, callable return/parameter qualifier facts, field/enum-payload qualifier facts, and callable bounded raw-pointer count-expression facts. The current-host compiler-port CTFE audit is complete, but this remains in-progress until the remaining parity, package preservation, and port-driven fact gaps are closed. See [13-comptime.md](13-comptime.md).
  - [x] Finish the compiler-port CTFE use-case audit against the current host compiler implementation, recording the required constants, generated tables, layout/range facts, structural facts, and compile-time branching forms. Audit reference: [../internal/ctfe-self-host-compiler-audit.md](../internal/ctfe-self-host-compiler-audit.md).
  - [~] Close evaluator parity gaps between `CompileTimeFunctionEvaluator` and the MIR/imported-template CTFE evaluator so type checking, MIR lowering, and cross-package typed template lowering accept and reject the same compile-time subset. MIR CTFE now preserves open structural-fact targets and default constants consistently with type-check CTFE, imported typed-template structural facts fold through the same `System.Compiler` evaluator after generic substitution, and source-free imported typed templates preserve ordinary unary `comptime` expressions over deterministic manifest-backed constants. Open integer `comptime` generic values now validate symbolically during template type checking, survive nested finite/law CTFE calls, and fold to concrete immediates after specialization.
  - [~] Close required ordinary-expression CTFE gaps found by the audit. Explicit conversion/cast evaluation, CTFE `try` propagation over role-marked result/option/status-shaped enums, integer arithmetic over typed `comptime` generic values, and nested finite/law calls that forward symbolic `comptime` values have landed; any remaining ordinary-expression work is driven by the CTFE closure pass and concrete compiler-port use.
  - [~] Close required package-image, source-bridge, and imported-template preservation gaps for CTFE forms used across package boundaries: conversions, structural facts, receiver calls, aggregate constants, `try` fallbacks, ordinary unary `comptime` expressions, and `comptime` generic substitution. CTFE `try` now publishes typed-template `try` expressions, ordinal-keyed propagation facts, source-bridge rendering, and imported-template MIR lowering; typed-template `System.Compiler` structural facts now preserve fact name, type arguments, and comptime value arguments and fold in imported MIR lowering; ordinary unary `comptime` expressions now publish/load/render as typed-template expressions and fold source-free when their operand is a deterministic manifest-backed constant or specialized integer `comptime` generic expression; source-free imported typed templates now resolve and fold manifest-backed direct finite/law calls with concrete `comptime` substitutions; published generic enum calls now resolve through direct enum-layout facts instead of display-name parsing.
        Typed package-template bodies are now authoritative across package boundaries: bridge source emits declaration-only APIs for typed templates, so corrupted or stale legacy `BodyText` cannot change imported generic specialization; the manifest-backed generic-body regression cluster is green.
- [ ] Close required `System.Compiler` structural-fact coverage from actual compiler-port queries, including callable/package/module facts, ABI/layout facts, field/enum/associated-type/doctrine facts, typed thread-safety law attribute facts, and wrong-target/out-of-range diagnostics. Type/field `[Grant]` / `[Deny]` law attribute count/name/kind/condition/type-predicate/type-metadata facts, implemented-trait count/type-predicate/type/metadata facts, declaration and actual `comptime` generic argument type-predicate/type/metadata facts, ordinary actual type argument predicate/metadata facts, method module identity facts, method `where` law predicate count/name/type-predicate/type/metadata facts, method parameter name facts, method generic trait-bound count/type-predicate/type/metadata facts, function-pointer/closure/method return and parameter nested-type metadata and qualifier facts, field/enum-payload nested-type metadata and qualifier facts, C source alias identity facts including closure return and parameter types, named type module identity facts, and function-pointer/closure/method bounded raw-pointer parameter count-expression facts now fold in CTFE, reject invalid runtime use, and are covered through package-backed typed aliases, package-backed trait metadata, generic type imports, field/enum-payload imports, or method imports.
  - [x] Complete runtime-boundary verification: structural fact calls and bare structural fact references now report STK3054 outside `comptime`; compile-time-only trait/doctrine/integer storage remains rejected in runtime contexts; scalar, aggregate, enum, layout, and structural-fact CTFE materialization is covered by targeted regressions that assert no compile-time calls leak into MIR/LLVM.
- [ ] Complete the self-hosting CTFE audit and closure pass: identify the exact compiler-port cases not covered by the current implementation, close only those gaps as complete tasks, verify package-image/source-bridge preservation for the required surface, and keep all structural facts compile-time-only and erased before backend lowering. See [13-comptime.md](13-comptime.md).
- [x] Implement explicit FFI ABI spelling and ABI-bearing function pointer types. Track detailed items in [15-ffi-abi.md](15-ffi-abi.md#9-implementation-work-items).
- [x] Add C-compatible struct layout attributes, packing, aggregate alignment, and packed-field safety rules. Track detailed items in [17-ffi-struct-layout.md](17-ffi-struct-layout.md#11-implementation-work-items).
- [x] Implement raw, multiline, and raw-interpolated string literal syntax for compiler text. See L08 in [01-language-feature-gaps.md](01-language-feature-gaps.md) and [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md#compiler-text-literals).
- [x] Implement richer switch-pattern ergonomics needed by the compiler port. Switch-label or-patterns (`case A | B:`), inclusive integer range patterns (`case 0..10:`), aggregate property patterns (`case Box { Field: pattern }:`), and exact-length list patterns (`case [first, second]:`) have landed with capture-consistency diagnostics, coverage/unreachable checks, guarded lowering, and typed package-image support. See L04 in [01-language-feature-gaps.md](01-language-feature-gaps.md) and [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md#pattern-matching).
- [x] Decide traversal and comptime-generic scope before the compiler port: add three explicit `for ... in ...` loop forms for borrowed element traversal, mutable borrowed element traversal, and indexed borrowed traversal; add typed `comptime` generic value parameters as Stark's const-generic spelling; do not add a general iterator protocol, `yield`, LINQ-style APIs, hidden iterator allocation, or hidden runtime dispatch. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [07-open-questions.md](07-open-questions.md), [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md#traversal-loops), and [13-comptime.md](13-comptime.md#23-comptime-generic-parameters).
- [x] Implement the three explicit traversal loop forms and keep collection APIs optimized for explicit count/index/slice access. See [01-language-feature-gaps.md](01-language-feature-gaps.md) and [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md#traversal-loops).
- [x] Implement typed `comptime` generic value parameters for the self-hosting integer slice: monomorphization identity, type/package-image preservation, diagnostics, fixed-array use sites, source bridge rendering, and imported-template substitution. Range-typed integer parameters support symbolic fixed-array lengths, fixed-array inference/type-checking, explicit integer value arguments at type/function call sites, symbolic forwarding with `comptime N`, scalar body materialization (`return N`), and package-image round trips through typed bodies and generated source. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md#comptime-generics), and [13-comptime.md](13-comptime.md#23-comptime-generic-parameters).

## Standard Library Work

- [ ] Migrate stdlib and compiler-port APIs to ordinary `Option<T>` and `Result<T, E>` where they replace nullable values, `Try*` out patterns, and recoverable failures. See [02-stdlib-gaps.md](02-stdlib-gaps.md) and [11-error-propagation.md](11-error-propagation.md).
- [ ] Add compiler-grade text building, formatting, escaping, and diagnostic rendering helpers. See [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [~] Implement the blessed generic collections and interning model: public `Hash` + `Eq` / `Ord` contracts for collections, text-key support, deterministic map/set or sorting paths, borrowed lookups, and compiler-internal typed interned IDs. `HashSet<T>` and exact ordinal text-key contracts have landed with the same key rule as `Dictionary<K,V>`. Track detailed items in [19-generic-collections-and-interning.md](19-generic-collections-and-interning.md#8-work-items); see also [02-stdlib-gaps.md](02-stdlib-gaps.md) and [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md).
- [ ] Implement bounded `i1024`/`u1024` compiler integer-fact helpers for range facts, enum tags, CTFE integer folding, SSA facts, known-bit masks, and overflow diagnostics. See [02-stdlib-gaps.md](02-stdlib-gaps.md) and [07-open-questions.md](07-open-questions.md).
- [ ] Add file read/write, filesystem metadata, recursive walk, temp directory, and path manipulation APIs. See [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add process spawning, environment, argv, working-directory, and output-capture APIs. See [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add reusable `System.Toml` parser/emitter support and use it for `Stark.toml`, `Stark.solution.toml`, and `Stark.user.toml` manifest decoding. Track detailed items in [21-system-toml.md](21-system-toml.md#7-work-items); see also [02-stdlib-gaps.md](02-stdlib-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md), and [07-open-questions.md](07-open-questions.md).
- [ ] Add package inspection/export text support and the JSON writer/reader pieces still needed for `stark inspect-pkg`, tests, and tooling. Binary remains the compiler load path. See [02-stdlib-gaps.md](02-stdlib-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md), [07-open-questions.md](07-open-questions.md), and [20-package-image-format.md](20-package-image-format.md#8-work-items).
- [x] Add `System.C` target-mapped C primitive aliases and `c_void` for FFI declarations. Track detailed items in [16-ffi-c-types.md](16-ffi-c-types.md#10-work-items).
- [~] Add `System.C` null-terminated C string helpers: borrowed views, owned strings, mutable output buffers, and explicit text conversions. Core helpers and literal/const `%s` FFI varargs validation are implemented; libLLVM-owned foreign string wrappers remain. Track detailed items in [18-ffi-c-strings.md](18-ffi-c-strings.md#11-work-items) and [23-libllvm-integration.md](23-libllvm-integration.md#8-work-items).
- [ ] Implement the compiler IR memory and fact model: typed handles, arena/table ownership scopes, fact categories, lowering policies, fact-transfer helpers, package-image durable facts, and phase-boundary verification. Track detailed items in [24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md#9-work-items); see also [02-stdlib-gaps.md](02-stdlib-gaps.md), [07-open-questions.md](07-open-questions.md), and [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [ ] Add the minimal threading coordination surface for future parallel build/test work: captured thread payloads, `System.Threading.Synchronized<T>` / `Locked<T>`, and MPSC channels. Track detailed items in [22-threading-coordination.md](22-threading-coordination.md#8-work-items); see also [02-stdlib-gaps.md](02-stdlib-gaps.md), [12-atomics.md](12-atomics.md), and [14-thread-safety-laws.md](14-thread-safety-laws.md).

## Tooling Work

- [ ] Add bootstrap staging around the existing C# host, Stage1 Stark compiler, and Stage2 Stark compiler. See [03-tooling-gaps.md](03-tooling-gaps.md) and [06-roadmap.md](06-roadmap.md).
- [ ] Make `stark build`, `stark run`, and `stark test` stage-aware. See [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Implement the formal `.stark/build/<profile>/<target-triple>/<stage>/` artifact layout and route package images, stdlib artifacts, native outputs, tests, diagnostics, and requested compiler artifacts into the stable subdirectories. Track detailed items in [25-build-artifact-layout.md](25-build-artifact-layout.md#7-work-items); see also [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Implement binary package-image generation/loading in Stark plus `stark inspect-pkg` JSON/text inspection output. Track detailed items in [20-package-image-format.md](20-package-image-format.md#8-work-items); see also [03-tooling-gaps.md](03-tooling-gaps.md) and [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Implement the blessed stdlib discovery order: explicit override, stage/build-local artifacts, repo source or `stdlib/dist` for development, then installed bundled stdlib next to the compiler. Track low-level tasks in [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md#phase-5-standard-library-bundling); see also [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Implement libLLVM backend integration through direct LLVM C API module construction, keeping textual LLVM only as a printed debug/inspection artifact. Track detailed items in [23-libllvm-integration.md](23-libllvm-integration.md#8-work-items); see also [03-tooling-gaps.md](03-tooling-gaps.md), [07-open-questions.md](07-open-questions.md), and [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Add native/libLLVM toolchain discovery, bundled-toolchain support, and target information, including C data-model and aggregate layout facts. Track target-specific detail in [16-ffi-c-types.md](16-ffi-c-types.md#10-work-items), [17-ffi-struct-layout.md](17-ffi-struct-layout.md#11-implementation-work-items), [23-libllvm-integration.md](23-libllvm-integration.md#8-work-items), and [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Add targeted diagnostic/artifact output for tests and debugging; keep broad logs, metrics, timings, and machine-readable CLI output out of the self-hosting critical path unless a concrete workflow needs them. See [03-tooling-gaps.md](03-tooling-gaps.md) and [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Keep VS Code and editor syntax/completions in sync when source syntax changes land. See [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Add release packaging, `stark doctor`, and clean-machine verification. See [03-tooling-gaps.md](03-tooling-gaps.md) and [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).

## Compiler Port

- [ ] Use SKILL.md the stark language skill for all implementation work.
- [ ] Implement the handwritten parser, parser facade, syntax model bridge, and text literal handling against canonical `Stark.g4`. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port diagnostics, compiler artifacts, pipeline orchestration, and artifact rendering. See [05-port-checklist.md](05-port-checklist.md) and [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [ ] Port module resolution, name binding, type resolution, overload resolution, and type compatibility facts. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port type checking, semantic validation, ownership validation, borrow liveness, range facts, enum facts, and compile-time evaluation. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port HIR/MIR lowering, drop lowering, switch lowering, imported-template handling, and related function-body builders. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port SSA lowering, SSA validation, and optimization passes. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port ABI lowering, LLVM IR emission, and native output support. See [05-port-checklist.md](05-port-checklist.md); track FFI detail in [15-ffi-abi.md](15-ffi-abi.md#9-implementation-work-items), [16-ffi-c-types.md](16-ffi-c-types.md#10-work-items), [17-ffi-struct-layout.md](17-ffi-struct-layout.md#11-implementation-work-items), and [18-ffi-c-strings.md](18-ffi-c-strings.md#11-work-items).
- [ ] Port package-image models, builders, loaders, bridge code, and shared codecs to the logical section model, with binary as the normal load format and JSON/text as inspection outputs. See [05-port-checklist.md](05-port-checklist.md) and [20-package-image-format.md](20-package-image-format.md).
- [ ] Port CLI, project driver, manifest handling, native-toolchain driver, and build entry points. See [05-port-checklist.md](05-port-checklist.md) and [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Port small fact helpers and assembly metadata helpers as leaf modules. See [05-port-checklist.md](05-port-checklist.md).

## Bootstrap And Cutover

- [ ] Build the first Stark compiler with the C# host compiler. See [06-roadmap.md](06-roadmap.md).
- [ ] Build the next Stark compiler with the first Stark compiler. See [06-roadmap.md](06-roadmap.md).
- [ ] Compare stage outputs, package images, diagnostics, and native artifacts for determinism. See [06-roadmap.md](06-roadmap.md).
- [ ] Run the ported Stark test suite against the self-hosted compiler. See [06-roadmap.md](06-roadmap.md).
- [ ] Keep the C# host compiler as the Stage0 builder until the Stark compiler can build itself. See [06-roadmap.md](06-roadmap.md).
- [ ] Update release archives to ship the Stark compiler, stdlib package, and required native tooling after cutover. See [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Document the migration bootstrap flow: C# host builds Stage1, Stage1 builds Stage2, then Stage2 becomes the trusted self-hosted compiler once tests and comparisons pass. See [06-roadmap.md](06-roadmap.md).
- [ ] During cutover, move the C# host compiler from `/src` to `/old_src`, let the Stark compiler own `/src`, and remove or demote the C# host from the normal build path while keeping an emergency recovery path. See [06-roadmap.md](06-roadmap.md).

## Open Decisions To Close

Keep the detailed options and trade-offs in [07-open-questions.md](07-open-questions.md). The decisions that most affect scheduling are:

- [x] Self-hosted source location.
- [x] Parser strategy.
- [x] Test-runner model.
- [x] Compiler artifact and diagnostic access for tests.
- [x] Optional/null replacement.
- [x] Comptime scope.
- [x] Traversal and comptime-generic scope.
- [x] Compiler integer fact domain.
- [x] Generic hashing and equality.
- [x] Package-image format.
- [x] TOML strategy.
- [x] Build-driver concurrency scope.
- [x] LLVM integration.
- [x] Bootstrap policy.
- [x] IR memory model.
- [x] Alias/noalias misuse policy.
- [x] `.stark/build/` layout.
- [x] Stdlib/package discovery.
- [x] VS Code/editor blocking status.

## Maintenance Rule

When work lands, update the checkbox here and the linked low-level checklist or
`Work Items` section. Add detailed evidence to the relevant companion document.
Book-facing tasks belong in `Book Updates`, even when the source spec also
mentions user-facing docs. Avoid adding design explanations, code samples, or
temporary code names to this file.
