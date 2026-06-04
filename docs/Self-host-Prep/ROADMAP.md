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
- Feature and architecture drafts: [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md), [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md), [10-traits-and-dynamic-dispatch.md](10-traits-and-dynamic-dispatch.md), [11-error-propagation.md](11-error-propagation.md), [12-atomics.md](12-atomics.md), [13-comptime.md](13-comptime.md), [14-thread-safety-laws.md](14-thread-safety-laws.md), [15-ffi-abi.md](15-ffi-abi.md), [16-ffi-c-types.md](16-ffi-c-types.md), [17-ffi-struct-layout.md](17-ffi-struct-layout.md), [18-ffi-c-strings.md](18-ffi-c-strings.md)
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
- [ ] Update the book and user-facing docs for `Transferable` / `Shareable` thread-safety laws when that design lands. Source spec: [14-thread-safety-laws.md](14-thread-safety-laws.md).

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
- [x] Draft the explicit FFI ABI syntax spec. See [15-ffi-abi.md](15-ffi-abi.md).
- [x] Draft the target-mapped C primitive alias spec for FFI. See [16-ffi-c-types.md](16-ffi-c-types.md).
- [x] Draft the C-style struct layout, packing, and alignment spec for FFI. See [17-ffi-struct-layout.md](17-ffi-struct-layout.md).
- [x] Draft the null-terminated C string interop spec for `System.C`. See [18-ffi-c-strings.md](18-ffi-c-strings.md).

## Test Infrastructure

- [ ] Decide and implement test discovery for Stark tests without relying on runtime reflection. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md) and [07-open-questions.md](07-open-questions.md).
- [ ] Add rich assertions for diagnostics, collections, ranges, null/option-like values, and type checks. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Add golden-file and snapshot helpers for diagnostics, LLVM, MIR, SSA, and package text. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Add temp directory, temp file, cleanup, and fixture-editing helpers. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md) and [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add process execution with stdout, stderr, exit-code, argv, environment, and working-directory capture. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md) and [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add a host-compiler target mode so Stark tests can run against the current C# compiler. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Add machine-readable compiler artifact and diagnostic inspection for deep pipeline tests. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md), [03-tooling-gaps.md](03-tooling-gaps.md), and [07-open-questions.md](07-open-questions.md).
- [ ] Add parameterized tests, platform gates, serial-test groups, and regression harness support. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Expand `System.Testing` to support the ported host test suite. See [02-stdlib-gaps.md](02-stdlib-gaps.md) and [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).

## Port The Tests First

- [ ] Port shared test helpers and fixtures. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port feature tests that primarily check LLVM text output. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port parser, diagnostic, type checking, ownership, MIR, SSA, LLVM, and package tests. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port pipeline artifact tests after artifact inspection exists. See [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md) and [05-port-checklist.md](05-port-checklist.md).
- [ ] Port standard-library tests in compile-only slices first, then runtime slices. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port CLI, project, package, native-toolchain, and integration tests once process/tooling support exists. See [03-tooling-gaps.md](03-tooling-gaps.md) and [05-port-checklist.md](05-port-checklist.md).
- [ ] Add Stark test-runner project files to replace the current xUnit runner configuration. See [03-tooling-gaps.md](03-tooling-gaps.md), [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md), and [05-port-checklist.md](05-port-checklist.md).

## Language Work

- [~] Finish the compile-time-only reuse surface: default method bodies, associated types, and doctrine-based `Hash`, `Eq`, `Ord`, and `Format` contracts. Default members and the explicit static `Dictionary<K,V>` key `Hash`/`Equals` contract have landed; associated types, `Ord`, and `Format` remain open. See [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md) and [10-traits-and-dynamic-dispatch.md](10-traits-and-dynamic-dispatch.md).
- [x] Finish cross-module trait conformance checks and generic-dispatch coverage. See [10-traits-and-dynamic-dispatch.md](10-traits-and-dynamic-dispatch.md).
- [ ] Decide how optional values replace host nullability and nullable C# APIs. See [01-language-feature-gaps.md](01-language-feature-gaps.md) and [07-open-questions.md](07-open-questions.md).
- [ ] Define alias/noalias proof carriers and require wrong-alias usage to be a compile-time diagnostic. See [01-language-feature-gaps.md](01-language-feature-gaps.md) and [07-open-questions.md](07-open-questions.md).
- [ ] Decide the parser strategy for the self-hosted compiler. See [01-language-feature-gaps.md](01-language-feature-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md), and [07-open-questions.md](07-open-questions.md).
- [ ] Finalize the `comptime` design and implementation scope. See [13-comptime.md](13-comptime.md).
- [x] Implement explicit FFI ABI spelling and ABI-bearing function pointer types. Track detailed items in [15-ffi-abi.md](15-ffi-abi.md#9-implementation-work-items).
- [x] Add C-compatible struct layout attributes, packing, aggregate alignment, and packed-field safety rules. Track detailed items in [17-ffi-struct-layout.md](17-ffi-struct-layout.md#11-implementation-work-items).
- [x] Implement raw, multiline, and raw-interpolated string literal syntax for compiler text. See L08 in [01-language-feature-gaps.md](01-language-feature-gaps.md) and [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md#compiler-text-literals).
- [~] Implement richer switch-pattern ergonomics needed by the compiler port. Switch-label or-patterns (`case A | B:`) have landed with capture-consistency diagnostics and native literal-switch lowering; range, list, and property patterns remain open. See L04 in [01-language-feature-gaps.md](01-language-feature-gaps.md) and [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md#pattern-matching).
- [ ] Decide which iterator, traversal, and const-generic ergonomics are needed before the compiler port. See [01-language-feature-gaps.md](01-language-feature-gaps.md).
- [ ] Keep concurrency language work scoped to what self-hosting actually needs first. See [14-thread-safety-laws.md](14-thread-safety-laws.md).

## Standard Library Work

- [ ] Standardize ordinary `Option<T>` and `Result<T, E>` usage across the stdlib and compiler port. See [02-stdlib-gaps.md](02-stdlib-gaps.md) and [11-error-propagation.md](11-error-propagation.md).
- [ ] Add compiler-grade text building, formatting, escaping, and diagnostic rendering helpers. See [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add string-key dictionaries, hash sets, deterministic maps/sets, sorting, searching, and symbol interning. See [02-stdlib-gaps.md](02-stdlib-gaps.md) and [08-stark-feature-roadmap.md](08-stark-feature-roadmap.md).
- [ ] Decide and implement the BigInt/range-facts support needed by the type checker. See [02-stdlib-gaps.md](02-stdlib-gaps.md) and [07-open-questions.md](07-open-questions.md).
- [ ] Add file read/write, filesystem metadata, recursive walk, temp directory, and path manipulation APIs. See [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add process spawning, environment, argv, working-directory, and output-capture APIs. See [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add TOML support for `Stark.toml` and `Stark.solution.toml`. See [02-stdlib-gaps.md](02-stdlib-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md), and [07-open-questions.md](07-open-questions.md).
- [ ] Add JSON support for package images unless the package format changes. See [02-stdlib-gaps.md](02-stdlib-gaps.md), [03-tooling-gaps.md](03-tooling-gaps.md), and [07-open-questions.md](07-open-questions.md).
- [~] Add `System.C` target-mapped C primitive aliases and `c_void` for FFI declarations. Track detailed items in [16-ffi-c-types.md](16-ffi-c-types.md#10-work-items).
- [ ] Add `System.C` null-terminated C string helpers: borrowed views, owned strings, mutable output buffers, and explicit text conversions. Track detailed items in [18-ffi-c-strings.md](18-ffi-c-strings.md#11-work-items).
- [ ] Decide the compiler IR memory strategy: arenas and handles, owned trees, or another explicit ownership model. See [02-stdlib-gaps.md](02-stdlib-gaps.md), [07-open-questions.md](07-open-questions.md), and [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [ ] Add the synchronization pieces needed after atomics, such as mutex, once, channels, and thread locals if they remain part of the chosen design. See [02-stdlib-gaps.md](02-stdlib-gaps.md), [12-atomics.md](12-atomics.md), and [14-thread-safety-laws.md](14-thread-safety-laws.md).

## Tooling Work

- [ ] Add bootstrap staging and snapshot compiler policy. See [03-tooling-gaps.md](03-tooling-gaps.md), [06-roadmap.md](06-roadmap.md), and [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Make `stark build`, `stark run`, and `stark test` stage-aware. See [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Formalize the `.stark/build/` artifact layout by profile, target, and stage. See [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Add package-image generation and loading in Stark. See [03-tooling-gaps.md](03-tooling-gaps.md) and [02-stdlib-gaps.md](02-stdlib-gaps.md).
- [ ] Add stdlib package build and discovery for self-hosting. See [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Decide LLVM integration for the self-hosted compiler. See [03-tooling-gaps.md](03-tooling-gaps.md), [07-open-questions.md](07-open-questions.md), and [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Add native toolchain discovery, bundled-toolchain support, and target information, including C data-model and aggregate layout facts. Track target-specific detail in [16-ffi-c-types.md](16-ffi-c-types.md#10-work-items), [17-ffi-struct-layout.md](17-ffi-struct-layout.md#11-implementation-work-items), and [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Add machine-readable diagnostics, logs, metrics, and artifact output where tests and tooling need them. See [03-tooling-gaps.md](03-tooling-gaps.md) and [04-test-infrastructure-audit.md](04-test-infrastructure-audit.md).
- [ ] Keep VS Code and editor tooling in sync with syntax that has landed. See [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Add release packaging, `stark doctor`, and clean-machine verification. See [03-tooling-gaps.md](03-tooling-gaps.md) and [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).

## Compiler Port

- [ ] Port grammar, parsing, syntax models, visitors, and text literal handling after the parser strategy is decided. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port diagnostics, compiler artifacts, pipeline orchestration, and artifact rendering. See [05-port-checklist.md](05-port-checklist.md) and [09-self-hosted-compiler-architecture.md](09-self-hosted-compiler-architecture.md).
- [ ] Port module resolution, name binding, type resolution, overload resolution, and type compatibility facts. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port type checking, semantic validation, ownership validation, borrow liveness, range facts, enum facts, and compile-time evaluation. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port HIR/MIR lowering, drop lowering, switch lowering, imported-template handling, and related function-body builders. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port SSA lowering, SSA validation, and optimization passes. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port ABI lowering, LLVM IR emission, and native output support. See [05-port-checklist.md](05-port-checklist.md); track FFI detail in [15-ffi-abi.md](15-ffi-abi.md#9-implementation-work-items), [16-ffi-c-types.md](16-ffi-c-types.md#10-work-items), [17-ffi-struct-layout.md](17-ffi-struct-layout.md#11-implementation-work-items), and [18-ffi-c-strings.md](18-ffi-c-strings.md#11-work-items).
- [ ] Port package-image models, builders, loaders, bridge code, and shared codecs. See [05-port-checklist.md](05-port-checklist.md).
- [ ] Port CLI, project driver, manifest handling, native-toolchain driver, and build entry points. See [05-port-checklist.md](05-port-checklist.md) and [03-tooling-gaps.md](03-tooling-gaps.md).
- [ ] Port small fact helpers and assembly metadata helpers as leaf modules. See [05-port-checklist.md](05-port-checklist.md).

## Bootstrap And Cutover

- [ ] Build the first Stark compiler with the C# host compiler. See [06-roadmap.md](06-roadmap.md).
- [ ] Build the next Stark compiler with the first Stark compiler. See [06-roadmap.md](06-roadmap.md).
- [ ] Compare stage outputs, package images, diagnostics, and native artifacts for determinism. See [06-roadmap.md](06-roadmap.md).
- [ ] Run the ported Stark test suite against the self-hosted compiler. See [06-roadmap.md](06-roadmap.md).
- [ ] Define snapshot compiler storage, provenance, checksums, and rollback policy. See [06-roadmap.md](06-roadmap.md) and [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Update release archives to ship the snapshot compiler, stdlib package, and required native tooling. See [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Document source bootstrap from a snapshot compiler. See [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md).
- [ ] Remove or demote the C# host compiler from the normal build path while keeping an emergency recovery path. See [06-roadmap.md](06-roadmap.md).

## Open Decisions To Close

Keep the detailed options and trade-offs in [07-open-questions.md](07-open-questions.md). The decisions that most affect scheduling are:

- [ ] Self-hosted source location.
- [ ] Parser strategy.
- [ ] Test-runner model.
- [ ] Compiler artifact and diagnostic access for tests.
- [ ] Optional/null replacement.
- [ ] BigInt scope.
- [ ] Generic hashing and equality.
- [ ] Package-image format.
- [ ] TOML strategy.
- [ ] Build-driver concurrency scope.
- [ ] LLVM integration.
- [ ] Bootstrap snapshot policy.
- [ ] IR memory model.
- [ ] Alias/noalias misuse policy.
- [ ] `.stark/build/` layout.
- [ ] Stdlib/package discovery.
- [ ] VS Code/editor cutover requirements.

## Maintenance Rule

When work lands, update the checkbox here and the linked low-level checklist or
`Work Items` section. Add detailed evidence to the relevant companion document.
Book-facing tasks belong in `Book Updates`, even when the source spec also
mentions user-facing docs. Avoid adding design explanations, code samples, or
temporary code names to this file.
