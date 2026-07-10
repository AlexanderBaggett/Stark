# Stage1 Package-Image Module Split

Status: short-lived execution tracker.

**Delete this document when every task below is complete.** Move any durable
architecture or package-format policy into
[`docs/Internals/PackageImage.md`](../Internals/PackageImage.md) instead of
keeping this tracker after the split.

## Goal

Split the Stage1 package-image implementation into focused Stark modules while
preserving the current package format, public behavior, and import surface.

The immediate target was the former 68k-line
`selfhost/Compiler/Mir/PackageImage.stark`, now reduced to a compatibility
facade. Its companion
`PackageCodec.stark` is already a useful shared byte-format boundary and should
not be folded back into the facade. The layout follows the existing C# host
compiler's `PackageImage/{Models,Shared,Builder,Loader,Bridge}` structure.

## Non-Goals

- Do not change the STARKPKG, `STRS`, `PINF`, `MANF`, or legacy MIR binary
  formats as part of a move-only slice.
- Do not change `inspect-pkg` output, package compatibility rules, or package
  discovery behavior.
- Do not merge unrelated logical-manifest or MIR-lowering work into a module
  move.
- Do not make `Compiler.Mir.PackageImage` an implementation dumping ground
  again after the facade is established.

## Target Layout

```text
selfhost/Compiler/Mir/
  PackageImage.stark                              // compatibility facade only
  PackageCodec.stark                              // temporary compatibility facade
  PackageImage/
    Models.stark                                  // Compiler.Mir.PackageImage.Models
    Shared/
      BinaryFormat.stark                          // fixed header, section directory, IDs
      TypeCodec.stark                             // legacy MIR byte + durable type-reference graph codec
      EnumLayoutCodec.stark                       // enum-layout fact encoding helpers
      LegacyMirCodec.stark                        // MIR/text/float/assembly compatibility codec
      GenericTemplatePublicationPolicy.stark      // publish/filter policy only
    Builder/
      PackageImageBuilder.stark                   // top-level logical-image assembly
      StringTableBuilder.stark                    // STRS indexing and payload construction
      ManifestJsonBuilder.stark                   // compatibility aggregate and public delegations
      ManifestTypeReferenceBuilder.stark          // shared manifest type-reference descriptors
      SourceSurfaceManifestBuilder.stark          // source-surface row accumulator
      TypedInterfaceManifestBuilder.stark         // typed-interface row accumulator
      CompilerFactsManifestBuilder.stark          // compiler-fact/native row accumulator
      GenericTemplateManifestBuilder.stark        // generic-template row accumulator
      ManifestJsonWriter.stark                    // reusable JSON member-writing primitives
      LegacyMirSectionWriter.stark                // MIR1/MIR2 and sectioned-MIR compatibility writers
      SourceSurfaceSectionBuilder.stark           // source declarations and bridge rows
      TypedInterfaceSectionBuilder.stark          // typed interface declaration rows
      CompilerFactsSectionBuilder.stark           // ABI/layout/effect/ownership/native facts
      GenericTemplateSectionBuilder.stark         // templates, bodies, operations, patterns
    Loader/
      PackageImageLoader.stark                    // header validation and load orchestration
      ManifestJsonReader.stark                    // shared manifest JSON shape/scalar readers
      SourceSurfaceSectionLoader.stark            // source-surface summaries and bridge graph
      TypedInterfaceSectionLoader.stark           // typed declaration/callable/type graphs
      CompilerFactsSectionLoader.stark            // ABI/layout/effect/ownership/native graphs
      GenericTemplateSectionLoader.stark          // templates, bodies, operations, patterns
      LegacyMirSectionLoader.stark                // MIR1/MIR2 and sectioned-MIR readers
    Bridge/
      PackageImageSourceBridge.stark              // package facts to source/module documents
    Inspection/
      PackageImageInspection.stark                // summary validation and text/JSON rendering
      PackageImageFiles.stark                     // byte-buffer and on-disk adapters
```

`PackageCodec.stark` is now a five-line forwarding facade over the `Shared`
modules so existing imports keep compiling. Remove it only after every caller
has moved to the focused modules and the facade exports an intentional
replacement.

## Boundary Rules

- `Compiler.Mir.PackageImage` remains the stable public facade. It may re-export
  focused public APIs or contain thin compatibility delegations, but owns no
  parsing, row materialization, or byte writing.
- `Models` contains data rows, enums, graph handles, summaries, and constructors;
  it does not read JSON, write bytes, inspect files, or call lowerers.
- `Shared` owns format constants and reusable codecs only. It cannot import
  `Builder`, `Loader`, `Bridge`, `Inspection`, or the facade.
- `Builder` writes models into byte/manifest sections. A builder never reads a
  package image to reconstruct a graph.
- `Loader` validates and materializes package sections. It never calls lowering
  or emits a package image.
- `Bridge` is the only layer that converts a loaded source-surface package model
  into source/module documents for compatibility paths.
- `Inspection` reads validated summaries and renders deterministic views; it does
  not materialize complete logical fact graphs.
- `LegacyMirSectionLoader` isolates old MIR1/MIR2/sectioned-MIR compatibility
  from the logical `STRS`/`PINF`/`MANF` loader path.
- No focused module imports `Compiler.Mir.PackageImage`; dependencies point
  inward to `Models` and `Shared`, never back through the facade.

## Baseline Inventory

- The facade currently has two direct source importers:
  `Compiler.Mir.SourceModuleLowering` and `Compiler.Mir.TestSupport`; it is
  also re-exported by `Compiler.Mir`.
- The first extracted public boundary is `PackageImageInspectionFormat`,
  `PackageImageValidationStatus`, and `PackageImageValidationStatusName` in
  `Compiler.Mir.PackageImage.Models`. The facade re-exports that module, so
  existing importers keep their original names.
- `PackageImageSummary` also now lives in `Models`; its missing-string default
  is model-local so `Models` does not depend on the binary-format module.
- `LogicalPackageFactsHeader` and the public `LogicalPackageImageFacts` now
  live in `Models`; the logical image configuration builder and its string table
  live under `Builder`.

## Work Plan

- [x] Record the existing public API and every importing module before moving
  code. Verify the facade with a focused `--check` of
  `Compiler.Mir.PackageImage` before and after every extraction.
- [x] Create the target directories and focused modules, then convert
  `Compiler.Mir.PackageImage` into an API-compatible facade with no behavioral
  change. The facade is now a 28-line compatibility module containing only
  focused-module re-exports and `SupportedPackageImageVersion`; it owns no
  parser, materializer, builder, inspection, or file-adapter implementation.
- [x] Extract `Models.stark`: logical-manifest rows, source-surface summaries,
  typed-interface summaries, compiler-fact graphs, generic-template graphs, and
  package-summary data. `Models` now owns the package-image status surface,
  package summaries and logical facts, every manifest row family, public model
  enums, declaration summaries, public graph models, and model-local
  constructors. The facade re-exports the model surface and retains no
  package-image model declarations.
- [x] Extract `Shared/BinaryFormat.stark` from the format-only parts of
  `PackageCodec.stark`; retain `PackageCodec.stark` as a forwarding compatibility
  module for this phase. The fixed binary format, section IDs, checked image
  reads, logical-string presence, byte conversion, and byte-copy helpers have
  moved. The remaining legacy MIR instruction/block/function/global, text,
  float, and assembly metadata codec also moved into
  `Shared/LegacyMirCodec.stark`; focused modules import their exact shared
  dependencies rather than the compatibility facade.
- [x] Extract `Shared/TypeCodec.stark`, `Shared/EnumLayoutCodec.stark`, and
  `Shared/GenericTemplatePublicationPolicy.stark` only after their dependencies
  are one-directional and individually checkable. The fixed-record enum-layout
  codec and the legacy MIR one-byte type codec now live in
  `Shared/EnumLayoutCodec.stark` and `Shared/TypeCodec.stark`, respectively,
  and are re-exported by `PackageCodec.stark`. `Shared/TypeCodec.stark` also
  owns the durable logical type-reference graph storage, text, and lookup
  helpers formerly embedded in the typed-interface loader. The generic-template
  publication predicates now live in their own Shared module and depend only
  on manifest visibility text rather than binding or typing models.
- [x] Extract `Builder/StringTableBuilder.stark` and
  `Builder/PackageImageBuilder.stark`, preserving string indices, section order,
  offsets, capacity reservation, and byte-for-byte output. The string table,
  logical image configuration builder (including string-presence checks), raw
  Brotli UTF-8 JSON payload encoder, and `STRS`/`PINF`/`MANF` section
  assembly have moved. The final top-level manifest module/target/native-
  dependency writers and JSON/Brotli encoders now also live in the package
  builder, leaving no byte- or JSON-writing body in the facade.
  `Builder/ManifestJsonWriter.stark` now owns the reusable JSON member-writing
  primitives so focused section builders can depend inward rather than on the
  facade. `Builder/ManifestJsonBuilder.stark` now preserves the established
  member-call surface as a six-field compatibility aggregate; focused family
  accumulators own all row storage and mutation, and
  `ManifestTypeReferenceBuilder.stark` owns reusable descriptor wiring.
- [x] Extract `Builder/LegacyMirSectionWriter.stark` as the compatibility
  writer boundary for MIR1/MIR2 and sectioned-MIR images. Its serializers have
  moved out of the facade without changing their exported signatures; the
  file-writing convenience APIs now live in `Inspection/PackageImageFiles.stark`.
- [x] Extract source-surface, typed-interface, compiler-fact, and generic-template
  section builders in that order. Builder/SourceSurfaceSectionBuilder.stark
  now owns source-declaration JSON row serialization and the per-module
  SourceSurface member writer. Builder/TypedInterfaceSectionBuilder.stark now
  owns typed-declaration JSON row serialization, including its named
  type-reference helper and per-module TypedInterface member writer.
  Builder/CompilerFactsSectionBuilder.stark now owns ABI, layout, effect,
  ownership, native-linkage JSON serialization, and the per-module
  CompilerFacts member writer. Builder/GenericTemplateSectionBuilder.stark now
  owns generic-template headers, typed bodies, expression/pattern trees,
  bound-operation helpers, and the CompilerSections wrapper; it remains
  internally partitioned by those groups. The focused manifest-family builders
  now own their corresponding row wiring and mutations; ManifestJsonBuilder is
  only their compatibility aggregate.
- [x] Extract `Loader/PackageImageLoader.stark` and
  `Loader/LegacyMirSectionLoader.stark`, preserving rejection behavior for
  malformed headers, directories, offsets, and section lengths. The primary
  loader now owns legacy magic recognition, fixed/sectioned/logical directory
  validation, logical facts and manifest-payload reads, and shared logical
  string-table lookup. `LegacyMirSectionLoader.stark` now owns the enum-layout,
  text, float, ordinary MIR, and MIR-with-assembly readers; the matching
  compatibility serializers now live in
  `Builder/LegacyMirSectionWriter.stark`. The primary loader also owns manifest
  summary/model orchestration and build-profile/target compatibility queries.
- [x] Extract and shrink `Builder/ManifestJsonBuilder.stark` as the
  dependency-breaking compatibility aggregate. It now owns only the shared
  string table, module names, four focused family builders, module fan-out, and
  thin public delegations. Source-surface, typed-interface, compiler-fact/
  native, and generic-template row storage and mutation live in their focused
  manifest builders; reusable type-reference descriptor construction lives in
  `ManifestTypeReferenceBuilder.stark`.
- [x] Extract source-surface, typed-interface, compiler-fact, and generic-template
  section loaders in the same order as their builders. Loader/ManifestJsonReader.stark
  now owns shared manifest JSON shape, scalar, text-member, and generic-array
  readers, and
  Loader/SourceSurfaceSectionLoader.stark owns source-surface node discovery,
  import/re-export checks, declaration lookup, and type-alias generic-parameter
  validation plus source row materialization into the source-surface graph.
  Loader/TypedInterfaceSectionLoader.stark now owns effective typed-interface
  selection plus typed declaration, callable, parameter, and type-reference
  node lookup, declaration/callable header text, and declaration/callable
  fact readers, iterative type-reference graph materialization, and type-alias
  fact graph materialization. It also owns callable fact graph mutators and
  materialization plus global fact text, constant-initializer, graph-mutator,
  and materialization helpers, the complete typed type graph, and the
  top-level typed-interface graph materializer. Its public declaration,
  callable, parameter, and type-reference JSON summary/materialization APIs
  now live in the focused loader and are re-exported by the facade.
  Loader/CompilerFactsSectionLoader.stark now owns compiler-fact section
  discovery plus function-effect, ABI, layout, native metadata/linkage,
  function-semantic, and function-ownership graph mutators, materializers,
  text APIs, and public JSON graph entry points. The facade re-exports it.
  Loader/GenericTemplateSectionLoader.stark owns generic-template graph
  mutators, text helpers, typed body/expression/pattern materialization, and the
  public JSON graph entry point. ManifestJsonReader owns the shared top-level
  module lookup. All four section loader families are complete and re-exported.
  Preserve all typed-handle ordinals and materialized graph shapes; leave
  compatibility bridge reconstruction for later slices.
- [ ] Extract `Bridge/PackageImageSourceBridge.stark`; keep source reconstruction
  and bridge-specific fallback behavior out of the ordinary loaders. Stage1
  currently materializes the bridge-compatible source-surface graph, but has no
  `LoadedModuleDocument`/source-document model or reconstruction implementation
  to move yet; port that boundary before claiming this item.
- [x] Extract `Inspection/PackageImageInspection.stark` and
  `Inspection/PackageImageFiles.stark`; preserve deterministic text/JSON output
  and the existing on-disk byte-buffer adapters. The summary reader and
  deterministic text/JSON renderer now live in
  `Inspection/PackageImageInspection.stark`; the byte-buffer, file adapters,
  and serializer-backed write convenience APIs now live in
  `Inspection/PackageImageFiles.stark`. The facade re-exports both focused
  inspection modules and owns none of these behaviors.
- [x] Add dependency-direction checks: `Shared -> Models` is allowed only where
  a codec truly needs a model; `Models -> Shared`, `Shared -> Builder/Loader`,
  and every child module -> facade are forbidden. The existing
  `scripts/check-selfhost-mir-dependencies.sh` now checks all focused files,
  rejects both PackageImage and PackageCodec facade imports, forbids operational
  layer imports from Shared, and forbids Shared imports from Models.
- [ ] After each move-only slice, run `--check` on the moved module, the
  `tests-stark/selfhost.Ir` package-image facts, `git diff --check`, and a
  before/after package-image byte comparison. Run the broader package-backed
  test slice once the split is complete. Focused checks, LLVM/Clang validation,
  dependency checks, diff hygiene, and the 80-fact PackageImage/PackageCodec/
  logical-package runtime slice are green; the explicit pre-split/post-split
  byte fixture comparison is the remaining part of this item.
- [ ] Update `TASKS.md` with the completed split only after the facade,
  dependency checks, and compatibility verification are all green.
- [ ] Delete this document and remove its references once every checkbox is
  complete.

## Verification Log

- `Compiler.Mir.PackageImage.Models`,
  `Compiler.Mir.PackageImage.Shared.BinaryFormat`,
  `Compiler.Mir.PackageImage.Shared.EnumLayoutCodec`,
  `Compiler.Mir.PackageImage.Loader.PackageImageLoader`,
  `Compiler.Mir.PackageImage.Loader.LegacyMirSectionLoader`,
  `Compiler.Mir.PackageImage.Inspection.PackageImageInspection`,
  `Compiler.Mir.PackageImage.Inspection.PackageImageFiles`,
  `Compiler.Mir.PackageImage.Builder.StringTableBuilder`,
  `Compiler.Mir.PackageImage.Builder.PackageImageBuilder`,
  `Compiler.Mir.PackageCodec`, and the public `Compiler.Mir.PackageImage`
  facade each pass focused `--check` after the first extractions. The facade
  retains the pre-existing 13 recursive generic-template writer warnings. The
  source-surface model extraction, enum-layout codec forwarding boundary, and
  both loader modules and both inspection modules all pass the same focused
  checks.
- The additional focused checks for
  `Compiler.Mir.PackageImage.Builder.LegacyMirSectionWriter`,
  `Compiler.Mir.PackageImage.Shared.TypeCodec`, the updated
  `Compiler.Mir.PackageImage.Inspection.PackageImageInspection`, the updated
  `Compiler.Mir.PackageImage.Inspection.PackageImageFiles`,
  `Compiler.Mir.PackageCodec`, and the public facade pass. `git diff --check`
  also passes, and no focused module imports the package-image facade.
- The source-surface, typed-interface, compiler-fact, and generic-template
  manifest-row extractions, together with the public model enum surface, pass
  focused checks for `Compiler.Mir.PackageImage.Models` and the public facade.
- The public function-effect, ABI, type-reference, and typed type-alias graph
  extractions also pass focused checks for `Compiler.Mir.PackageImage.Models`
  and the public facade.
- The concrete-layout, native-metadata, and function-semantic graph extractions
  also pass focused checks for `Compiler.Mir.PackageImage.Models` and the
  public facade.
- The function-ownership and typed-callable graph extractions also pass focused
  checks for `Compiler.Mir.PackageImage.Models` and the public facade.
- The typed-global and typed-type graph extractions also pass focused checks
  for `Compiler.Mir.PackageImage.Models` and the public facade.
- The generic-template graph and aggregate manifest-model extractions also
  pass focused checks for `Compiler.Mir.PackageImage.Models` and the public
  facade.
- The declaration-summary extraction completes the `Models` task; the focused
  checks for `Compiler.Mir.PackageImage.Models` and the public facade pass.
- `Compiler.Mir.PackageImage.Builder.ManifestJsonBuilder` and the public
  facade pass focused `--check` after the logical-manifest accumulator and
  type-reference descriptor wiring move. The new builder imports only
  `Compiler.Ir`, `PackageCodec`, `StringTableBuilder`, `Models`, and
  `System.Text`; it does not depend on the package-image facade. The facade
  retains its pre-existing 13 recursive generic-template writer warnings.
  `stark test` also passes for `tests-stark/selfhost.Ir`.
- `Compiler.Mir.PackageImage.Builder.SourceSurfaceSectionBuilder` and the
  public facade pass focused `--check` after moving all source-surface JSON
  row writers and the per-module SourceSurface member writer. The module
  depends only on manifest-builder/writer and string-table Builder modules,
  `Models`, `System.Json`, and `System.Text`; it does not import the facade.
  `stark test` also passes for `tests-stark/selfhost.Ir`.
- `Compiler.Mir.PackageImage.Builder.TypedInterfaceSectionBuilder` and the
  public facade pass focused `--check` after moving all typed-interface JSON
  row writers, the named type-reference helper, and the per-module
  TypedInterface member writer. It has the same inward-only Builder/Models and
  `System.Json`/`System.Text` dependencies, and `stark test` passes for
  `tests-stark/selfhost.Ir`.
- `Compiler.Mir.PackageImage.Builder.CompilerFactsSectionBuilder` and the
  public facade pass focused `--check` after moving ABI, layout, function
  effect/semantic/ownership, and linkage JSON writers with the per-module
  CompilerFacts member writer. It adds only the inward `Compiler.Ir`
  dependency for linkage tables, does not import the facade, and `stark test`
  passes for `tests-stark/selfhost.Ir`.
- `Compiler.Mir.PackageImage.Builder.GenericTemplateSectionBuilder` and the
  public facade pass focused `--check` after moving generic-template headers,
  typed bodies, expression/pattern trees, bound operations, and the
  CompilerSections wrapper. It imports the one-way
  `CompilerFactsSectionBuilder` type-reference JSON helper rather than
  duplicating it, does not import the facade, and `stark test` passes for
  `tests-stark/selfhost.Ir`.
- `Compiler.Mir.PackageImage.Loader.ManifestJsonReader`,
  `Compiler.Mir.PackageImage.Loader.SourceSurfaceSectionLoader`, and the public
  facade pass focused `--check` after moving common manifest JSON readers and
  source-surface node discovery. Both modules are facade-independent, and
  `stark test` passes for `tests-stark/selfhost.Ir`.
- The same loader modules and public facade pass focused `--check` after moving
  source-surface row materialization (including the source-surface graph
  materializer) and its two generic array validators. `stark test` passes for
  `tests-stark/selfhost.Ir`.
- `Compiler.Mir.PackageImage.Loader.ManifestJsonReader`,
  `Compiler.Mir.PackageImage.Loader.TypedInterfaceSectionLoader`, and the
  public facade pass focused `--check` after moving top-level module lookup and
  typed-interface/declaration/
  callable/type-reference node access. The typed loader imports only Models,
  the shared reader, Compiler.Ir for work tables, and System.Json; typed fact
  graph materialization remains a later slice.
- The same typed loader and public facade pass focused `--check` after moving
  typed declaration/callable header text and declaration/callable fact readers.
  The loader now imports System.Text directly; typed graph materialization
  remains a later slice.
- The same typed loader and public facade pass focused `--check` after moving
  iterative type-reference graph materialization, its graph mutators, and typed
  type-alias fact graph materialization. It now imports System.Memory directly
  for the graph text-buffer mutators; callable, global, and type fact graphs
  remain separate loader slices.
- The same typed loader and public facade pass focused `--check` after moving
  callable fact graph mutators/materialization and global fact text,
  constant-initializer, graph-mutator, and materialization helpers. The global
  text-buffer helper and its row/initializer mutators now live together in the
  typed loader with no duplicate facade declarations; typed type graph
  materialization remains the next slice.
- The same typed loader and public facade pass focused `--check` after moving
  the complete typed type graph mutators/materialization and the top-level
  typed-interface graph materializer. Structural checks confirm those
  declarations are absent from the facade and no focused loader imports it.
- The typed loader and public facade pass focused `--check` after moving all
  public typed/type-reference JSON summary and graph-materialization APIs.
  The facade now re-exports TypedInterfaceSectionLoader, preserving its public
  import surface while owning none of those parser/materializer bodies.
- `Compiler.Mir.PackageImage.Loader.CompilerFactsSectionLoader` and the public
  facade pass focused `--check` after moving compiler-fact section discovery
  and the function-effect/ABI graph families, including their public text and
  JSON materialization APIs.
- The same compiler-facts loader and facade pass focused `--check` after moving
  the complete layout, native metadata/linkage, function-semantic, and
  function-ownership graph families. Structural checks confirm their mutators
  and public JSON materializers are absent from the facade, and no focused
  loader imports it.
- `Compiler.Mir.PackageImage.Loader.GenericTemplateSectionLoader` passes a
  focused check after moving the complete generic-template graph mutator,
  typed-body/expression/pattern materialization, text, and public JSON API
  families. The facade no longer owns a generic-template loader declaration.
- `Compiler.Mir.PackageImage.Loader.SourceSurfaceSectionLoader` and
  `Compiler.Mir.PackageImage.Loader.TypedInterfaceSectionLoader` pass focused
  checks after receiving their remaining public JSON summary APIs. The facade
  preserves those APIs through explicit re-exports.
- `Compiler.Mir.PackageImage.Loader.PackageImageLoader` passes a focused check
  after receiving manifest summary/model orchestration and package fact
  matching. Loader orchestration now calls each focused section loader directly.
- `Compiler.Mir.PackageImage.Builder.PackageImageBuilder` passes a focused
  check after receiving the remaining top-level manifest JSON writers and JSON/
  Brotli payload encoders. The public facade also passes focused `--check` after
  shrinking to 27 lines of re-exports plus its version compatibility shim.
- `Compiler.Mir.PackageImage.Shared.LegacyMirCodec` passes a focused check
  after receiving the remaining MIR instruction/block/function/global, text,
  float, and assembly metadata codec. `Compiler.Mir.PackageCodec` is now a
  five-line forwarding facade and also passes its focused check. The stale
  direct PackageCodec import was removed from `SourceModuleLowering`; only the
  intentional public re-export from `Compiler.Mir` remains.
- `Compiler.Mir.PackageImage.Builder.ManifestJsonBuilder`,
  `Compiler.Mir.PackageImage.Loader.PackageImageLoader`,
  `Compiler.Mir.PackageImage.Builder.LegacyMirSectionWriter`, and
  `Compiler.Mir.PackageImage.Loader.LegacyMirSectionLoader` pass focused checks
  after replacing PackageCodec imports with exact Shared-module imports.
- `Compiler.Mir.PackageImage.Builder.SourceSurfaceManifestBuilder` and
  `TypedInterfaceManifestBuilder` pass focused checks after receiving their
  row tables, per-module initialization, and 31/33 mutation implementations.
  Their section writers and the compatibility aggregate pass focused checks
  with the original public member-call API intact.
- `Compiler.Mir.PackageImage.Builder.ManifestTypeReferenceBuilder` and
  `CompilerFactsManifestBuilder` pass focused checks after receiving reusable
  descriptor wiring and 52 effect/semantic/ownership/ABI/layout/native/linkage
  mutations. The compiler-facts section writer and top-level package writer
  also pass with direct access to the focused accumulator.
- `Compiler.Mir.PackageImage.Builder.GenericTemplateManifestBuilder` passes a
  focused check after receiving all generic-template row storage, 110 public
  mutation implementations, and its private top-level call-signature helper.
  `ManifestJsonBuilder.stark` is reduced from 11,066 to 3,327 lines and passes
  as a six-field compatibility aggregate with thin public delegations. The
  generic-template section writer passes with its pre-existing 13 recursive
  writer warnings.
- The public `Compiler.Mir.PackageImage` facade passes focused `--check` after
  all four manifest-family accumulators and the shared descriptor helper were
  extracted. Public builder signatures remain available through the aggregate.
- `Compiler.Mir.PackageImage.Shared.TypeCodec`, the typed-interface,
  compiler-facts, and generic-template loaders, and the 28-line public facade
  pass focused checks after moving 599 lines of type-reference graph storage,
  text, and lookup code out of the typed-interface loader. The new
  `Shared.GenericTemplatePublicationPolicy` also passes focused `--check`.
  The dependency guard now matches the documented boundary by allowing a
  Shared codec to depend on Models while still rejecting every operational
  layer and compatibility facade.
- `scripts/check-selfhost-mir-dependencies.sh` passes with PackageImage layer
  enforcement enabled; structural scans also find no focused module importing
  the PackageImage or PackageCodec compatibility facade.
- `Compiler.Mir.PackageImage.Builder.ManifestJsonWriter` and the public facade
  pass focused checks after extracting the reusable JSON member-writing
  primitives.
- The missing `System.Runtime.Platform.SetPermissions` dispatcher was restored;
  `System.FileSystem` and `tests-stark/selfhost.Ir/IrTests.stark` now pass
  focused `--check` again (with only the three pre-existing recursive
  FileSystem warnings).
- Every focused module under `Compiler/Mir/PackageImage` emits LLVM, and Clang
  accepts every emitted module. That sweep covers all builder, loader, model,
  shared-codec, inspection, and extracted manifest-family modules.
- The sweep exposed two host LLVM liveness/materialization defects rather than
  package-format changes: direct pointer-backed borrow calls could consume a
  skipped retborrow temporary load, and a scalar extraction could consume a
  skipped deferred aggregate insert. Both emitter paths now preserve the
  required SSA values and have focused C# regressions; the two targeted tests
  pass together.
- A cold `tests-stark/selfhost.Ir` build now emits both `libStarkCompiler.a`
  and `libStarkCompiler.starkpkg`, clearing the two LLVM verifier failures that
  originally blocked the split validation.
- The typed-interface callable summary path now reads comptime generic-
  parameter type references for both functions and methods. Its focused
  `--check`, emitted LLVM, and Clang verification pass; the package header/
  module-shell mega-fact exercises the function path, and the typed-declaration
  type-reference payload fact exercises the method path plus the existing
  wrong-source rejection cases.
- Stale package-fact expectations were aligned with already-established model
  behavior: generic-template statement storage/provenance produces nine text
  rows, and an explicit empty `SourceSurface` shadows legacy module arrays.
  The complete 80-fact PackageImage/PackageCodec/logical-package runtime slice
  passes, including legacy and logical round trips, deterministic inspection,
  malformed-image rejection, every materialized graph family, and file
  adapters.

## Completion Criteria

- The public `Compiler.Mir.PackageImage` import remains source-compatible.
- Focused modules compile independently without dependency cycles.
- Existing package images still load, new images remain byte-identical for the
  same inputs, and inspection text/JSON remains deterministic.
- Legacy MIR loading, logical package loading, source bridging, and file adapters
  have separate focused tests.
- No source file in the split exceeds a coherent responsibility; in particular,
  generic-template builder and loader code remain partitioned by graph family.
