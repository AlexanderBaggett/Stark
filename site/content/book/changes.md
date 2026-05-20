+++
title = "Book Changes"
weight = 5
book_part = "Book Metadata"
book_status = "draft"
next = "/book/01-why-stark/"
+++

# Book Changes

This page records user-facing changes to the published Stark Book.

## v1.35 Draft

- Updated hosted entrypoint examples to use safe `export fn main` unless the
  entrypoint itself needs unsafe or foreign ABI features.
- Added generated Chapter Checkpoints to numbered tutorials so each chapter
  ends with concrete outcomes drawn from its steps.
- Added generated Lesson Paths to numbered tutorials so readers see the
  chapter route before starting.
- Added a book-structure guard to the site build so numbered chapters keep
  tutorial steps, examples, navigation, and no placeholder prose.
- Restructured numbered chapters as explicit tutorial steps and inserted the
  missing Performance Tuning and Unsafe Stark chapters before diagnostics.
- Reworked the standard-library, generated-IR, command-line-tool, and current
  boundary material so it reads as a buildable tutorial path instead of a
  planning note or reference summary.
- Reworked the remaining core-language, package/boundary, ABI/numeric,
  diagnostics, and project chapter step headings into action-oriented tutorial
  instructions.
- Reworked the early-language, arrays/text, testing, performance-model,
  performance-tuning, and unsafe chapters so their step headings also read as
  tutorial actions instead of topic labels.
- Added a dedicated closures tutorial after the borrowing comparison and
  renumbered later core, standard-library, performance, diagnostics, generated
  IR, and project chapters through chapter 37, with aliases for the shifted
  draft URLs.
- Added second checked code examples to chapters that only had one sample so
  every numbered chapter now has multiple examples.
- Created the website book section from the canonical outline.
- Added draft Part I chapters for installing, compiling, and reading first
  Stark programs.
- Added draft borrowing chapters, including a Stark-versus-Rust comparison.
- Added checked book samples so code snippets can stay aligned with the
  compiler.
- Added negative sample support for examples that should be rejected by the
  compiler.
- Added a single-file Markdown export for indexing, review, and printable
  workflows.
- Expanded the remaining Part II core-language chapters for aggregates, enums,
  fixed arrays, slices, and text views.
- Expanded the remaining Part III boundary chapters for generics, callable
  values, raw pointers, FFI, and native package metadata.
- Added draft standard-library chapters for console/process basics, memory and
  collections, files/filesystem/text, and threading/TCP.
- Added draft performance and systems chapters covering Stark's performance
  model, ABI boundaries, numeric policy, diagnostics, and generated IR.
- Added draft project chapters for command-line text tools, multi-module
  packages, file-processing utilities, native-backed packages, and performance
  case studies.
- Added draft quick-reference appendices for keywords, operators, integer
  ranges, function kinds, storage, manifests, unsupported features, and
  migration notes for Rust, C#, and C programmers.
- Added chapter-specific reference links from book pages to the language
  reference, standard library docs, and canonical examples.
- Added checked examples for result-shaped error handling and today's
  executable-as-test book sample pattern.
- Added a checked command-line text-tool core sample that keeps argument
  handling honest until hosted entrypoint arguments land.
- Added checked storage/lifetime and package-surface samples, and connected
  the file-processing project chapter to its checked standard-library sample.
- Connected the multi-module project chapter to the checked package-surface
  sample as its single-file starting point.
- Added a checked fixed-array tight-loop sample for the performance model,
  generated-IR, and performance case-study chapters.
- Added checked sample callouts to the storage quick reference and Rust, C#,
  and C migration appendices.
- Added a checked operators sample and strengthened the integer, function-kind,
  and unsupported/future-feature appendices with concrete examples.
- Connected the native-backed package project chapter to the checked FFI/raw
  pointer sample and added a native-package review checklist.
- Added a checked negative borrowing sample for plain borrow return escape and
  linked it from the borrowing and diagnostics chapters.
- Added a checked generic `Option<T>` enum sample to the generics chapter.
- Added a checked negative capturing-lambda sample to clarify the current
  callable-value boundary.
- Added checked positive and negative `frozen` examples to clarify deep
  read-only access.
- Expanded the memory-layout chapter with the checked aggregate layout sample
  to separate source-facing field access from C ABI promises.
- Added real manifest file includes for the installing chapter and manifest
  appendix so TOML examples stay aligned with checked-in examples.
- Added a checked keyword-tour sample to the keyword appendix.
- Added a checked fixed-capacity text interpolation sample to the arrays,
  slices, text, and views chapter.
- Added a dedicated checked bindings/control-flow sample with `for willexit`,
  `continue`, `break`, and integer `switch` examples.
- Added a checked negative function-guarantee sample showing that a
  `finite law` function cannot hide shared-state reads behind a general `fn`.
- Added a checked negative integer-range sample showing that implicit narrowing
  from `i32[min max]` to a smaller range is rejected.
- Added a checked negative immutable-local sample to the bindings and control
  flow chapter.
- Added checked switch-pattern samples covering guarded captures, guarded
  discards, and unreachable default arms.
- Added a checked negative trait-runtime-value sample to clarify that traits
  are compile-time contracts, not runtime dispatch objects.
- Added a checked negative function-pointer kind sample showing that a general
  `fn` is not promoted to a `finite law` callable value.
- Added a checked doctrine-facts sample showing doctrine members called through
  the doctrine name without creating a runtime doctrine object.
- Added a checked negative enum ABI boundary sample to clarify that ordinary
  Stark enums are not automatic `export`/FFI representations.
- Added a checked negative raw-pointer mutability sample showing that readonly
  raw access cannot be strengthened into mutable raw access.
- Added a checked memory-separation sample showing an `if disjoint(...)` fast
  path with an overlap-safe fallback.
- Added const-parameter provenance and current `independent` loop-contract
  boundary notes to the performance model chapter.
- Added bounded raw pointer region coverage to the FFI/raw-pointer and
  performance chapters, including region expressions, unsafe raw slice
  construction, and independent raw-pointer loop contracts.
- Added a checked bounded raw pointer sample covering copy, fill, transform,
  and overlap-safe fallback paths.
- Expanded callable-value coverage so `fnptr<finite ...>`, `fnptr<law ...>`,
  and `fnptr<finite law ...>` are described as higher-order semantic
  guarantees, and added a checked `finite law` function-pointer sample.
