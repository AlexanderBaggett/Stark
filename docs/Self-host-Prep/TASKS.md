# Compiler Work Tracker

This is the only work-tracking document intentionally retained in
`docs/Self-host-Prep`. It consolidates the unfinished work formerly spread
across the numbered preparation notes, roadmaps, checklists, and test ledger.
Durable contracts and design rationale live under `docs/Userfacing`,
`docs/StandardLibrary`, and `docs/Internals`.

Status as of 2026-07-16: self-host implementation is paused while the Stage0
release is prepared. Do not interpret the pause as a design rollback. Stage0
remains the source of truth until the bootstrap and parity gates in this file
pass.

Legend:

- `[ ]` not started or no verified implementation
- `[~]` partially implemented; the remaining clause is the task
- `[x]` completed migration/coordination gate retained for immediate context

Delete this file and the empty `docs/Self-host-Prep` directory when every task
below is complete or deliberately transferred to a successor release tracker.

## 1. Stage0 Release (Active Priority)

Durable contracts:

- [Release Archive Layout](../Internals/ReleaseArchiveLayout.md)
- [SDK Layout and Resolution](../Internals/SdkLayoutAndResolution.md)
- [Installing the Stark SDK](../Userfacing/InstallingTheStarkSdk.md)
- [Vendor Library API Coverage](../Internals/VendorLibraryApiCoverage.md)

### Supported releases and host contract

- [ ] Select the v1 supported release target IDs and record the support tier for
  macOS arm64, Linux x64, and Windows x64.
- [ ] Record minimum host requirements per supported target, including the
  platform SDK/runtime pieces that cannot be redistributed.
- [ ] Qualify Windows SDK/UCRT discovery and diagnostics on Windows.
- [ ] Define the allowlisted Stage0 private backend programs, runtime libraries,
  and Clang resource directories required for each release target; include only
  those paths in archive assembly and verification.
- [ ] Add an archive size budget and report the compiler/.NET runtime, compiler-
  private backend, System packages, Vendor packages, examples/docs, and installer
  payload separately.

### Compiler-private backend and host linkage

- [~] Replace whole-tree LLVM staging with a per-target allowlisted Stage0 private
  backend and ensure compiler object-emission operations use its archive-relative
  paths by default. The release payload is an internal backend runtime, not a
  public LLVM toolchain.
- [~] Finish compiler-private backend discovery, compatible-version diagnostics,
  and integrity reporting. Stage0 may retain trimmed private Clang while it emits
  textual LLVM; direct libLLVM object emission remains the Stage1 path below.
- [ ] Preserve explicit `--linker`, `--archiver`, `--target`, LLVM, SDK, and
  native-package override paths and test that each override wins deliberately.
- [ ] Improve missing-tool and missing-platform-SDK diagnostics so they name the
  attempted path, active target, and corrective action.
- [ ] Split private backend resolution from final host-link/native-C resolution;
  keep both routed through structured resolvers without letting ambient LLVM
  silently replace the packaged backend.
- [ ] Diagnose the documented Odin-style host layer independently: Xcode Command
  Line Tools/Xcode on macOS, MSVC/Windows SDK on Windows, and supported Clang plus
  system development libraries on Linux.

### SDK and official packages

- [~] Complete canonical executable discovery on macOS; qualify the same bounded
  executable-relative rules on Linux and Windows without project-ancestor scans.
- [~] Make compiler target detection, project output layout, package builders,
  SDK descriptors, and native-package selection consume one structured target
  identity.
- [~] Build `System.*` and every advertised official `Vendor.*` package for every
  supported release target from source as release inputs.
- [~] Complete Raylib packages for Linux x64 and Windows x64, including native
  archives, required runtime files, licenses, and platform link arguments.
- [ ] Validate that every official module has exactly one indexed package owner
  and that its package image carries complete ABI, optimization, linkage, and
  native metadata.
- [ ] Report unavoidable host prerequisites separately from corrupt/missing SDK
  payload errors in both human and JSON `stark doctor` output.

### Assembly, smoke tests, and publishing

- [~] Finish the release assembly workflow for the conventional `<sdk>/bin`
  layout without copying unchecked build leftovers.
- [ ] Keep release script modes minimal and documented: clean build, assemble,
  verify, and publish inputs must not silently imply one another.
- [~] Extract every supported archive outside the checkout with Stark overrides
  cleared and only the extracted SDK's `bin` on `PATH`.
- [~] From that clean environment, build and run a fresh external System-only
  application.
- [~] From that clean environment, build and link a fresh external
  `Vendor.Raylib` application and verify the selected wrapper/native archives
  and required system frameworks/libraries.
- [ ] Add compatibility tests that reject stale compiler/package/native ABI
  combinations, including AArch64 integer-like aggregate carrier mismatches.
- [ ] Complete `stark doctor` recommendations, archive-integrity coverage, and
  structured release-version checks.
- [ ] Cover non-LTO, ThinLTO, explicit overrides, packaged private-backend
  selection, and intended host-link fallback paths in the release smoke matrix.
- [ ] Update contributor release instructions and the release-notes template.
- [ ] Gate publishing on the supported-platform archive matrix.

Release acceptance:

- [ ] A user can extract a supported archive and run `stark --help` without .NET.
- [ ] With only the documented platform-development prerequisite installed, a
  user can compile a basic application without installing a separate LLVM SDK or
  version-matched Stark backend.
- [ ] Official `System.*` and `Vendor.*` imports resolve from `sdk.json` with no
  checkout paths, ambient `pkg-config`, or project-specific setup.
- [ ] `stark doctor --strict` explains the selected SDK, packaged private backend,
  host link layer, and every missing non-bundled prerequisite independently.

## 2. Self-Host Resume Guardrails

- [x] Migrate durable Self-host-Prep documentation to canonical Userfacing,
  StandardLibrary, and Internals documents and consolidate tracking here.
- [ ] Revalidate Stage0 tests and the most recent Stage1 component baselines after
  the Stage0 release branch settles.
- [ ] Refresh task statuses against the release commit before resuming; do not
  treat old progress-log prose as proof of current behavior.
- [ ] Keep `/src` as the maintained C# Stage0 source and `/selfhost` as the Stark
  compiler source; release selection must not rename or erase either tree.
- [ ] Keep the frozen pre-self-host `comptime` baseline. Broad structural CTFE is
  post-bootstrap work and must not be pulled onto the critical path casually.
- [ ] Preserve explicit ownership, alias, layout, ABI, range, linkage, and
  optimization facts at every compiler phase and package boundary.
- [ ] Treat accepted-but-unlowerable source as a compiler bug, not a supported
  diagnostic path.

## 3. Stage1 Frontend

Architecture: [Self-hosted Compiler Architecture](../Internals/SelfHostedCompilerArchitecture.md).

### Parsing and syntax

- [~] Complete lexer/parser coverage and parity for all Stage0-supported syntax,
  attributes, declarations, expressions, types, FFI forms, and recovery paths.
- [~] Finish syntax models, source spans, diagnostics, generated-test-runner
  discovery inputs, and deterministic parse output.
- [ ] Add differential fixtures for every parser construct and diagnostic class.

### Binding and typing

- [~] Complete module/import/name binding, overload resolution, generics,
  traits/doctrines, visibility, attributes, constants, and extern declarations.
- [~] Complete type resolution, range/fact inference, definite assignment and
  return, ownership/borrow rules, effects, and callable-value checking.
- [~] Split the remaining oversized binding implementation into focused modules;
  keep source files below roughly 5,000 lines where a coherent boundary exists.
- [~] Split the remaining oversized typing implementation by resolution,
  inference, conversions, calls/generics, effects, ownership, and diagnostics.
- [ ] Replace hot-path text keys with typed interned IDs and ensure deterministic
  output never depends on hash-table iteration order.
- [ ] Add focused unit and differential tests for every split so module movement
  cannot change semantics or diagnostics.

## 4. MIR, SSA, ABI, and LLVM Backend

Durable contracts:

- [IR Memory and Fact Model](../Internals/IrMemoryAndFactModel.md)
- [LLVM Backend Integration](../Internals/LlvmBackendIntegration.md)
- [Language Internals](../Internals/LanguageInternals.md)

### Typed IR ownership and fact model

- [~] Introduce phase-specific typed handles (`TypeId`, `SymbolId`, `MirValueId`,
  `MirBlockId`, `SsaValueId`, and peers) over compiler-owned dense tables.
- [~] Complete fact categories and declarations for attach point, producer,
  consumer, durability, serialization, and preserve/translate/consume/recompute/
  forbid-drop policy.
- [ ] Add low-friction fact-transfer builders and phase-boundary validation for
  stale handles, wrong handle kinds, and accidental `forbid-drop` losses.
- [ ] Preserve all durable ABI/layout/linkage/optimization facts through package
  image write/load and reject malformed or incompatible fact sections.
- [ ] Add typed-handle, fact-transfer, optimization, package-round-trip, and LLVM
  emission tests.

### HIR and MIR

- [ ] Complete HIR/MIR lowering for every accepted expression, statement,
  declaration, callable, generic instantiation, imported body, and FFI form.
- [ ] Lower ownership, borrowing, moves, copies, drops, cleanup on every exit,
  alias proofs, memory contracts, range facts, and `comptime` results explicitly.
- [ ] Validate MIR control flow, types, ownership/drop completeness, and fact
  integrity before SSA lowering.

### SSA and optimization

- [~] Complete SSA construction, PHIs, dominance, memory operations, calls,
  globals, aggregates, conversions, checks, and exceptional diagnostic exits.
- [ ] Preserve/translate alias scopes, noalias groups, alignment, dereferenceable
  bytes, nonnull, ranges, call memory effects, loop independence, and provenance.
- [ ] Implement and test constant folding/propagation, dead code elimination,
  CFG simplification, copy propagation, range-driven check elimination,
  devirtualization/specialization, inlining policy, and loop optimization facts.
- [ ] Validate SSA after construction and every fact-invalidating optimization.

### ABI and libLLVM

- [~] Complete ABI lowering for supported targets, including aggregates,
  sret/byval/inreg carriers, calling conventions, variadics, alignment, and C
  primitive aliases.
- [~] Wire the existing typed LLVM C API wrappers into real MIR/SSA module,
  function, global, instruction, metadata, verification, and object emission.
- [ ] Attach every proven backend fact in the strongest sound LLVM form; never
  drop a fact merely because textual LLVM output is easier.
- [ ] Keep textual LLVM as deterministic inspection/golden output from the
  in-memory module, never as the production object-emission bridge.
- [ ] Add target-native ABI/FFI tests and negative tests for verifier errors,
  unavailable targets, wrong LLVM versions, and owned diagnostic disposal.

## 5. Package Images, SDK, CLI, and Build Driver

- [~] Complete Stage1 package-image models, builders, binary codec, loaders,
  imported generic/template bodies, and deterministic `inspect-pkg` views.
- [ ] Validate compatibility, target/profile/layout facts, checksums, section
  skipping, string/name tables, and exact package ownership during load.
- [~] Finish artifact routing under
  `build/<profile>/<target-triple>/<stage>/` for Stage1/Stage2 stdlib,
  diagnostics, native outputs, package images, tests, and inspection artifacts.
- [ ] Move package-image, artifact-inspection, and stage-comparison tests to the
  formal artifact layout.
- [ ] Complete the Stage1 CLI/project/solution driver: build, run, test, clean,
  inspect, doctor, target/profile/stage selection, and actionable diagnostics.
- [ ] Finish typed manifest decoding through `System.Toml`; remove any private
  TOML parser path and complete required TOML conformance coverage.
- [~] Port SDK manifest models, strict JSON loading, canonical-root discovery,
  target compatibility, indexed package selection, link plans, integrity checks,
  and cache identity to Stage1.
- [ ] Verify Stage1 package emission follows the same one-owner module rule as
  Stage0.
- [~] Run the same relocatable System/Raylib SDK fixture under Stage0 and Stage1.
- [ ] Require the SDK smoke matrix before selecting Stage1 for a release.
- [ ] Implement root-module pipeline incrementality or equivalent per-module/
  per-function reuse; unchanged dependency caching alone does not make a cold
  Stage1 compiler build viable.

## 6. Standard Library and Porting APIs

- [~] Finish the compiler-facing `Option<T>` / `Result<T,E>` migration and remove
  sentinel/error conventions that obscure control flow or ownership.
- [~] Complete ordered map/set, deterministic sorting helpers, text builder,
  typed interning, filesystem, process, environment, JSON, package inspection,
  TOML, and threading surfaces required by the port.
- [ ] Complete safe bounded/owned C-string wrappers used by LLVM and other FFI,
  including explicit foreign-owned copy/dispose patterns.
- [ ] Complete public spawned-process handles, chunked pipe reads, monotonic
  clocks, deadline waits, and process-tree termination on supported platforms.
- [ ] Keep platform-specific FFI inside explicit stdlib/native boundaries; the
  Stage1 driver must not grow private operating-system calls.
- [ ] Add deterministic tests and Userfacing/StandardLibrary documentation for
  any new public API before considering the port dependency closed.

## 7. Verification and Development Performance

- [ ] Resolve all remaining `tests-stark` failures after their parent compiler or
  stdlib features land; do not mask unsupported cases with broad skips.
- [ ] Close test-scope hygiene: every skip must name a platform/feature condition
  and every temporary fixture must be isolated from stale package artifacts.
- [ ] Keep the per-test progress/timeout protocol byte-compatible across Stage0
  and Stage1 and wire the landed Stage1 runner/driver components into `stark test`.
- [ ] Add differential parser, diagnostics, MIR, SSA, LLVM inspection, package,
  executable-behavior, and failure-path suites.
- [ ] Add performance baselines for frontend phases, lowering/optimization,
  package loading, incremental rebuilds, linking, and representative programs.
- [ ] Track peak memory and cold/warm build time; investigate regressions before
  expanding the bootstrap matrix.
- [ ] Sync editor syntax, completions, and diagnostics with the qualified Stage0
  language surface.

## 8. Vendor Binding Coverage

These tasks summarize the incomplete binding groups documented and mechanically
audited in [Vendor Library API Coverage](../Internals/VendorLibraryApiCoverage.md).
Every new binding group requires ownership/lifetime design, package metadata,
examples where useful, Stage0 tests, and Stage1 tests once expressible.

### STB

- [ ] Add STB decode constants, types, and metadata carriers.
- [ ] Add missing memory and filename decode APIs.
- [ ] Add global and thread-local decode controls.
- [ ] Add callback and `FILE *` decode APIs.
- [ ] Decide and implement STB zlib-helper exposure policy.
- [ ] Add file writers and writer controls.
- [ ] Add callback and memory writers.
- [ ] Add resize constants and simple resize APIs.
- [ ] Add the medium-complexity resize API.
- [ ] Add the extended resize API and reusable sampler support.
- [ ] Decide whether to enable conditional STB APIs.
- [ ] Update STB examples.
- [ ] Add Stage0 C# compiler/integration coverage.
- [ ] Add Stage1 Stark coverage.

### Miniaudio

- [ ] Add a generated miniaudio API inventory test.
- [ ] Add core constants and ABI carriers.
- [ ] Add full device/context/capture/duplex/loopback support.
- [ ] Add VFS and data-source support.
- [ ] Add audio-buffer and ring-buffer support.
- [ ] Add conversion, resampling, channel-map, and volume helpers.
- [ ] Add DSP, filtering, panning, fading, and spatialization.
- [ ] Expand decoding and embedded-decoder support.
- [ ] Re-enable and bind encoding.
- [ ] Re-enable and bind waveform/noise generation.
- [ ] Re-enable and bind the resource manager.
- [ ] Re-enable and bind the node graph.
- [ ] Re-enable and bind the engine, sounds, and sound groups.
- [ ] Update Miniaudio examples.
- [ ] Add Stage0 C# compiler/integration coverage.
- [ ] Add Stage1 Stark coverage.

### cgltf

- [ ] Add generated cgltf API inventory tests.
- [ ] Vendor and build the upstream writer header.
- [ ] Add complete enum coverage.
- [ ] Add complete indexed data-model views.
- [ ] Add parse/load/options coverage.
- [ ] Add zero-copy and owned-memory parse modes.
- [ ] Add accessor payload APIs.
- [ ] Add transform APIs.
- [ ] Add public index-helper coverage.
- [ ] Add extras and extension APIs.
- [ ] Add material/texture/image/sampler coverage.
- [ ] Add skin/camera/light/animation coverage.
- [ ] Add writer APIs.
- [ ] Update cgltf examples.
- [ ] Add Stage0 C# compiler/integration coverage.
- [ ] Add Stage1 Stark coverage.

### GLFW

- [ ] Add generated GLFW API inventory tests.
- [ ] Add complete constant and enum coverage.
- [ ] Add initialization/platform/options coverage.
- [ ] Add monitor, video-mode, and gamma APIs.
- [ ] Add complete window-management APIs.
- [ ] Replace or extend the event bridge for full callback coverage.
- [ ] Add input, cursor, and clipboard APIs.
- [ ] Add joystick and gamepad APIs.
- [ ] Add OpenGL context/proc/timer APIs.
- [ ] Complete Vulkan helper APIs.
- [ ] Add native-access APIs.
- [ ] Update GLFW examples.
- [ ] Add Stage0 C# compiler/integration coverage.
- [ ] Add Stage1 Stark coverage.

### SQLite

- [ ] Complete safe lifetime-checked borrowed value/column views.
- [ ] Add loadable-extension packaging and callback support.
- [ ] Add virtual-table and custom-VFS support.
- [ ] Define allocator/mutex/pcache callback-table lifetime ownership.
- [ ] Complete retained introspection, utility, and compatibility APIs.

### SDL3

- [ ] Add generated SDL3 API inventory tests.
- [ ] Establish the raw binding module boundary.
- [ ] Add lifecycle, logging, errors, hints, and assertion support.
- [ ] Add video, displays, windows, surfaces, and pixels.
- [ ] Add renderer, texture, blend, rect, and color support.
- [ ] Add GPU support.
- [ ] Add audio support.
- [ ] Add events, keyboard, mouse, touch, pen, joystick, gamepad, sensor, and haptics.
- [ ] Add filesystem, storage, process, dialog, and dynamic-library support.
- [ ] Add threading, synchronization, atomics, and timers.
- [ ] Add properties, environment, locale, power, clipboard, and miscellaneous utilities.
- [ ] Add safe equivalents for retained macros and inline helpers.
- [ ] Add SDL3 examples.
- [ ] Add Stage0 C# compiler/integration coverage.
- [ ] Add Stage1 Stark coverage.

## 9. Bootstrap, Parity, and Release Adoption

- [ ] Build the Stage1 Stark compiler with Stage0 and emit the expected compiler
  executable and package set.
- [ ] Build the Stage2 Stark compiler with Stage1 from a clean stage layout.
- [ ] Compare Stage1/Stage2 package images, diagnostics, MIR/SSA/LLVM inspection
  artifacts, linked executables, and runtime behavior deterministically.
- [ ] Resolve every unexplained bootstrap divergence; record intentional
  nondeterminism explicitly and minimize it.
- [ ] Run Stage0, Stage1, and Stage2 compiler and generated-code benchmarks.
- [ ] Require Stage1 to match or beat Stage0 on the agreed performance suite, or
  document and approve a bounded transition exception with a removal task.
- [ ] Publish the qualified self-hosted compiler, stdlib, vendor packages, and
  native tooling while retaining an explicitly buildable Stage0 recovery path.
- [ ] Add CI and release smoke coverage for explicit compiler-stage selection.

## 10. Deferred Post-Self-Host Work

- [ ] Rebuild broad structural `comptime` / `System.Compiler` in Stark after
  bootstrap, with conformance, termination, determinism, caching, and diagnostic
  tests; do not introduce runtime reflection or hidden allocation.
- [ ] Revisit structural fact enumeration and generic compile-time branching only
  after measured compiler needs justify the surface.
- [ ] Revisit broader threading/concurrency features only after the narrow
  compiler coordination APIs have proven insufficient.
- [ ] Complete the explicit bitfield design and reconcile it with implemented
  field-offset, packing, alignment, ABI, borrow, and backend layout behavior.
- [ ] Migrate the compiler-private LLVM 22.1.x backend dependency to the qualified
  stable LLVM 23.1.x release and repeat ABI, object, optimizer, archive, runtime-
  closure, and performance qualification without expanding it into a public LLVM
  development distribution.
