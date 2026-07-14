# Phase 31 - Relocatable SDK And Bundled Vendor Resolution

Status: **The Stage0 SDK/package implementation path is complete in the current
worktree: canonical manifest discovery, indexed resolution, reserved
namespaces, structured target compatibility, explicit stage manifests, package
ownership/identity, precise SDK diagnostics, checksum-complete System/Raylib
payloads, informational-version stamping, and clean release assembly are
implemented. A 153 MiB macOS arm64 archive using the accepted
`<sdk-root>/bin/stark` layout has passed strict doctor plus PATH-only System
and Raylib build/link smoke before and after a physical move, with only
`<sdk-root>/bin` on `PATH`. Linux/Windows qualification remains. Stage1
now has an owned manifest/loader, root and target validation, indexing,
streamed file integrity, activation, selected link planning,
relocation-invariant cache identity, a shared relocation fixture, and a
package-ownership selection boundary; compiler-driver/package-emission wiring
and real cross-stage build/link runtime qualification remain (2026-07-13).**

This document is the implementation strategy and progress tracker for making
the standard library and official vendor library reliable parts of every Stark
distribution. It closes the gap between the accepted archive layout in
[28-release-archive-layout.md](28-release-archive-layout.md) and the actual
compiler, package, release, and smoke-test behavior.

> **Short-lived tracker:** delete this file only after every Stage0 and Stage1
> work item and verification-matrix row is complete and the durable SDK
> contract has been moved to user-facing and internals documentation. Do not
> delete it merely because the Stage0 implementation can build a local sample.

Related documents:

- [20-package-image-format.md](20-package-image-format.md)
- [25-build-artifact-layout.md](25-build-artifact-layout.md)
- [28-release-archive-layout.md](28-release-archive-layout.md)
- [ToolchainPackagingRoadmap.md](ToolchainPackagingRoadmap.md)
- [TASKS.md](TASKS.md)

Tracking rules:

- Use `[x]` for complete, `[~]` for partially implemented, and `[ ]` for open.
- Update the checkboxes and the progress log together when a slice lands.
- Record verification commands or test names in the progress log rather than
  expanding task descriptions into a second implementation diary.
- Do not derive a completion percentage from checkbox counts; the work items
  intentionally differ in size and risk.

## 1. Product Contract

The required user experience is:

1. Download and extract one target-specific Stark SDK archive.
2. Add the extracted SDK's `bin` directory to `PATH`.
3. Run `stark doctor` and receive either `status: ok` or a precise platform
   prerequisite diagnostic.
4. Create an ordinary `Stark.toml` project.
5. Write `import System...` or `import Vendor.Raylib` and build without adding
   SDK libraries to `[dependencies]`, passing `-I`, configuring `STARK_PATH`,
   installing Raylib development packages, or invoking `pkg-config`.
6. Move the extracted SDK to another directory and repeat the build without
   changing project or machine configuration.

`System.*` and official `Vendor.*` modules are compiler-distribution assets.
Application manifests must not list them as package dependencies. Native
metadata belongs to the vendor package that owns the FFI boundary, so the
application does not repeat linker libraries, framework arguments, include
directories, or runtime-file staging rules.

This contract applies to all normal entry points:

```text
stark build
stark run
stark test
stark App.stark --check
stark App.stark --emit-exe
```

The contract must be identical under the retained Stage0 compiler and the
self-hosted compiler before Stage1 is selected for releases.

## 2. Baseline Failure Analysis

The audit that led to this plan found six independent gaps. This section
preserves that starting point; several gaps are now addressed by the Stage0
work recorded in Section 5 and the progress log.

### 2.1 Compiler installation root is inferred from the wrong executable

The repository `stark` launcher executes
`src/bin/Debug/net10.0/compiler.dll`. Stage0 uses `AppContext.BaseDirectory`
and its parent as installed-library candidates, so it searches relative to the
nested DLL rather than the launcher or repository root. A project outside the
repository therefore cannot find the repository's `vendor` directory.

A self-contained release executable under `<sdk-root>/bin` cannot treat its
own directory as the SDK root: `sdk.json` and the bundled assets intentionally
live one level above it. Guessing siblings or walking arbitrary parents would
still be a layout coincidence rather than an explicit SDK identity contract,
and would not solve symlinks, development launchers, or Stage1 selection.

### 2.2 Release assembly copies vendor state instead of building it

The release workflow builds the compiler and System package, then passes the
checked-out `vendor` directory to `scripts/package-release.ps1`. It does not
build every official vendor package for the release target. Release contents
therefore depend on stale, ignored, or workstation-generated files.

### 2.3 Target identities disagree

The release workflow currently names macOS arm64 as
`aarch64-apple-darwin`, while checked-in/generated Raylib artifacts use
`arm64-apple-macosx26.0.0`. Linux release jobs use
`x86_64-unknown-linux-gnu`, while the Raylib package uses
`x86_64-pc-linux-gnu`. Package target validation is exact-string based, so the
release job and package builder do not agree on selectable artifacts. No
target-scoped Windows Raylib package is currently present.

### 2.4 Required native artifacts are not release inputs

Native archives such as the current macOS `libVendorRaylib.a` and
`native/raylib/libraylib.a` match the repository-wide `*.a` ignore rule. A
local checkout may contain them while a clean release checkout does not. The
release must build or acquire pinned native artifacts into a clean staging
directory; it must never depend on ignored workstation files.

### 2.5 Release smoke tests bypass the product contract

The archive smoke test asserts that `vendor/` exists but never imports an
official `Vendor.*` module. Its System runtime probe supplies an explicit
`-I <archive>/stdlib/dist`, and its native-package test creates a synthetic C
shim rather than consuming an SDK package. These probes can pass while normal
automatic SDK discovery is broken.

### 2.6 Package ownership and resolver state are ambiguous

Current Stage0-generated vendor package images can contain transitive
`System.*` modules. For example, both the Raylib and GLFW images contain
`System.BitOperations`. This creates duplicate module owners, larger package
images, and resolution results that depend on search order.

The project driver also stores package/source kind as presentation text. The
native-source suppression index recognizes only state `"package images"`,
while target packages are labelled `"target package images"`. A target
package can therefore fail to suppress source-native fallback, causing an
installed package to unexpectedly re-enter source manifests or `pkg-config`.

## 3. Implementation Architecture

### 3.1 A Stark distribution is an explicit SDK

Every distributable compiler has one SDK root and one versioned runtime SDK
manifest. The compiler discovers the SDK root from the canonical path of its
own `<sdk-root>/bin/stark[.exe]` executable and loads `<sdk-root>/sdk.json`.

Resolution precedence is:

1. `--sdk-root <directory>` for explicit compiler, bootstrap, and CI use.
2. `STARK_SDK_ROOT` as an advanced environment override.
3. The parent of the canonical real executable directory for the conventional
   `<sdk-root>/bin/stark[.exe]` layout, with executable-directory manifest
   compatibility limited to the generated repository development launcher.
4. A hard SDK-not-found diagnostic.

Normal installation requires only `PATH`; neither override is part of user
setup, and the public `PATH` entry is `<sdk-root>/bin`. The executable path must
be canonicalized through symlinks. On Windows, the implementation uses the
actual process image path rather than `argv[0]`. Executable-relative discovery
is bounded to the two documented layouts; it never walks application or
arbitrary compiler ancestors.

The generated repository Stage0 launcher passes the repository root explicitly
as its development SDK root. Repository source fallback is enabled by a
development SDK manifest, not by walking ancestors of the application project.
Stage-local bootstrap packages are represented by a stage SDK manifest rather
than another implicit search tier.

`STARK_PATH` remains a low-level direct-compiler search escape hatch. It is not
an SDK discovery mechanism and project builds continue to ignore it.

### 3.2 `sdk.json` is the runtime contract

`release.json` records release provenance. `sdk.json` records the runtime data
needed by the compiler. Keeping these responsibilities separate lets a
repository development SDK and a packaged release use the same compiler path
without pretending the repository is a published release.

The versioned SDK manifest contains at least:

- SDK schema, Stark version, compiler compatibility, and package-format
  version.
- Distribution kind: `release`, `development`, or `stage`.
- Canonical SDK target ID and structured target/ABI facts.
- Full LLVM target triple, data layout, baseline CPU/features, relocation
  model, code model, C data model, pointer width, endianness, and minimum OS
  facts where applicable.
- Exact module-to-package ownership index.
- Package identity, version, profile, API/content hash, and dependency list.
- Relative package-image and static-library paths.
- Relative native include, library, runtime-file, and license paths.
- Native libraries and ordered linker arguments, including framework pairs.
- Checksums for all required SDK artifacts.
- Optional development source roots, permitted only in a development SDK.

All runtime paths are slash-normalized, relative to the SDK root, contain no
parent traversal, and resolve inside the canonical SDK root. Absolute paths in
a release SDK are assembly errors.

Conceptual shape:

```json
{
  "schemaVersion": 1,
  "kind": "release",
  "sdkVersion": "0.1.0",
  "compilerCompatibility": "stark-sdk-v1",
  "packageFormatVersion": 2,
  "target": {
    "id": "macos-arm64",
    "llvmTriple": "arm64-apple-macosx...",
    "dataLayout": "...",
    "architecture": "arm64",
    "operatingSystem": "macos",
    "abi": "darwin",
    "pointerBitWidth": 64,
    "endianness": "little",
    "baselineCpu": "generic",
    "baselineFeatures": [],
    "relocationModel": "pic",
    "codeModel": "small",
    "cDataModel": "lp64",
    "minimumOperatingSystemVersion": "11.0"
  },
  "modules": [
    { "name": "System", "package": "System" },
    { "name": "Vendor.Raylib", "package": "Vendor.Raylib" },
    { "name": "Vendor.Raylib.Core", "package": "Vendor.Raylib" }
  ],
  "packages": [
    {
      "id": "System",
      "version": "0.1.0",
      "profile": "release",
      "apiHash": "...",
      "contentHash": "...",
      "image": "stdlib/dist/macos-arm64/System.starkpkg",
      "library": "stdlib/dist/macos-arm64/libSystem.a",
      "dependencies": [],
      "native": {
        "artifacts": [],
        "includeDirectories": [],
        "libraryDirectories": [],
        "runtimeFiles": [],
        "licenseFiles": [],
        "libraries": [],
        "linkArguments": []
      }
    },
    {
      "id": "Vendor.Raylib",
      "version": "6.0",
      "profile": "release",
      "apiHash": "...",
      "contentHash": "...",
      "image": "vendor/dist/macos-arm64/libVendorRaylib.starkpkg",
      "library": "vendor/dist/macos-arm64/libVendorRaylib.a",
      "dependencies": [
        { "id": "System", "apiHash": "...", "contentHash": "..." }
      ],
      "native": {
        "artifacts": ["vendor/dist/macos-arm64/native/raylib/libraylib.a"],
        "includeDirectories": ["vendor/dist/macos-arm64/native/raylib"],
        "libraryDirectories": ["vendor/dist/macos-arm64/native/raylib"],
        "runtimeFiles": [],
        "licenseFiles": ["vendor/dist/macos-arm64/native/raylib/LICENSE"],
        "fileChecksums": [
          {
            "path": "vendor/dist/macos-arm64/native/raylib/libraylib.a",
            "sha256": "..."
          }
        ],
        "libraries": ["raylib"],
        "linkArguments": []
      }
    }
  ]
}
```

The strict Stage0 schema uses arrays for `modules`, `packages`, package
dependencies, and native metadata. The assembler sorts these arrays before
deterministic text emission. The shortened checksum above represents the
required release checksum coverage for every image, archive, native artifact,
header/source payload, runtime file, and license. API/content hashes bind each
release descriptor to the corresponding package image, while dependency hashes
bind the exact package graph. The example is not a license to omit
package/backend facts already preserved by package images.

### 3.3 Preserve the existing library roots

The first implementation keeps `stdlib/` and `vendor/` at the SDK root so the
accepted release layout and source organization remain recognizable. Commands
and compiler runtime support are isolated under `bin/`:

```text
<sdk-root>/
  bin/
    stark[.exe]
    <compiler runtime support files>
  sdk.json
  release.json
  stdlib/
    src/
    templates/
    dist/<sdk-target>/
      System.starkpkg
      libSystem.a | System.lib
  vendor/
    src/
    native/
    licenses/
    dist/<sdk-target>/
      libVendorRaylib.starkpkg
      libVendorRaylib.a | VendorRaylib.lib
      native/raylib/...
  toolchain/...
```

Even though a release archive contains only one target, keeping the target
directory makes development and release package paths identical. The runtime
resolver follows `sdk.json`; it does not scan this layout to guess ownership.

### 3.4 Target compatibility uses structured facts

One target-descriptor implementation is shared by target detection, package
building, SDK assembly, package validation, output paths, `doctor`, and release
jobs. Release scripts consume compiler-produced target JSON instead of
hardcoding a second spelling.

The SDK target ID is a stable distribution selector such as `macos-arm64`,
`linux-x64`, or `windows-x64`. It is distinct from the full LLVM target triple.
Package compatibility is validated from structured ABI facts, including
architecture, operating system, ABI/environment, pointer width, endianness, C
data model, data layout, and deployment minimum. Raw triple spelling alone is
not the compatibility key.

Package images retain the complete backend target facts. A compatible
application CPU-feature selection may be a superset of the SDK package's
baseline features; incompatible ABI or required-feature changes require a
separate package variant.

### 3.5 Each bundled module has one package owner

The SDK assembler rejects duplicate module ownership. `System.*` modules are
owned by the System package. A vendor package contains only its source-owned
`Vendor.<Name>...` modules and records package dependencies by package ID plus
API/content hash. It does not copy System or sibling-vendor modules into its
own image.

Official SDK namespaces are reserved during normal builds. Explicit compiler
development modes may select another SDK root or disable SDK lookup, but an
ordinary application `-I` directory cannot silently shadow `System.*` or
official `Vendor.*` modules.

Only the imported package dependency graph contributes archives and native
link facts. Importing Raylib does not link GLFW, SDL3, SQLite, or unused vendor
packages.

### 3.6 SDK resolution belongs in the compiler core

The compiler creates one `SdkPackageResolver` and composes it with ordinary
project/package resolution. Project mode no longer manufactures SDK `-I`
arguments through directory heuristics. This gives direct-file mode and
project mode the same package identity, target checks, native metadata, and
diagnostics.

The module index provides deterministic direct lookup and avoids recursively
scanning every package image on compiler startup. Package manifests are still
validated when loaded, and a checksum/content-hash mismatch is an SDK
integrity failure.

Proposed normal precedence:

1. Source files belonging to the current project.
2. Declared ordinary project dependencies.
3. The active stage SDK when a stage compiler is selected.
4. Official modules from the active SDK index.
5. Explicit development source fallback declared by a development SDK.

The reserved `System.*` and `Vendor.*` families skip ordinary dependency
shadowing unless an explicit SDK-development override is active.

### 3.7 Official native packages are complete release artifacts

Release assembly builds or acquires every advertised vendor package from
pinned inputs in a clean staging directory. An official package carries all
redistributable libraries, headers, shims, runtime files, and licenses needed
by consumers. It records concrete relative native paths and ordered linker
arguments in the package image/SDK manifest.

`pkg-config` and machine-local include/library paths may be used while
developing or rebuilding a binding. They must be resolved before release
assembly and must not remain runtime requirements of an official release
package. Unavoidable operating-system runtime/framework requirements are
declared in SDK metadata and diagnosed by `stark doctor`; development packages
must not be a hidden requirement.

Normal release builds never silently fall back from a missing binary SDK
package to vendor source. Source rebuild is an explicit developer operation.

### 3.8 Backend and optimization facts remain authoritative

SDK indexing is a discovery layer, not a reduced package format. Package images
remain the authoritative source for typed interfaces, generic templates,
function effects, calling conventions, ABI carriers, integer ranges, alias and
memory facts, alignment/dereferenceability, strict/fast floating-point policy,
inline/hot/cold hints, target features, linkage, and native link metadata.

The implementation must:

- Preserve package facts unchanged through SDK lookup and package loading.
- Compare package and from-source LLVM output for backend-fact parity.
- Build SDK packages in release profile with the intended optimization and LTO
  policy.
- Preserve archive and native-library link order.
- Keep ThinLTO and section/dead stripping available where supported.
- Avoid loading or linking unimported vendor packages.
- Benchmark indexed SDK startup/resolution against the current directory scan
  so the fix does not regress compiler latency.

### 3.9 Diagnostics are SDK-specific and actionable

`stark doctor` reports:

- SDK root and whether it came from CLI, environment, executable, or stage.
- SDK kind, schema, compiler compatibility, and package-format version.
- Canonical SDK target plus full backend/ABI facts.
- Every advertised official package and whether its package image, archive,
  native payload, runtime files, and checksums are valid.
- Platform requirements that cannot be redistributed.

`stark doctor --strict` exits nonzero for a missing, incompatible, or corrupt
required SDK artifact. A machine-readable form is required for release
automation.

An unresolved official import distinguishes:

- package not included in this SDK,
- package unsupported for the selected target,
- package present but corrupt/incomplete,
- package incompatible with the active compiler/package format, and
- explicit development source fallback failure.

Official-import diagnostics do not suggest `STARK_PATH` as normal remediation.

## 4. Implementation Map

Stage0 should use focused files under `src/Compiler/Sdk/`:

```text
SdkRootResolver.cs
SdkManifest.cs
SdkManifestLoader.cs
SdkPackageIndex.cs
SdkTargetDescriptor.cs
SdkTargetCompatibility.cs
SdkIntegrityValidator.cs
SdkDiagnostics.cs
```

Primary Stage0 integration points:

- `src/Compiler/CompilerCli.cs`
- `src/Compiler/ProjectCliDriver.cs`
- `src/Compiler/ModuleResolution.cs`
- `src/Compiler/TargetCompatibilityValidator.cs`
- `src/Compiler/NativeToolchain.cs`
- `src/compiler.csproj` launcher generation
- package-image builder/loader dependency metadata

Stage1 should mirror the conceptual boundary under `selfhost/Compiler/Sdk/`:

```text
RootResolution.stark
ManifestModel.stark
ManifestLoading.stark
PackageIndex.stark
TargetDescriptor.stark
TargetCompatibility.stark
IntegrityValidation.stark
Diagnostics.stark
```

Stage1 uses `System.Json` for `sdk.json` and must produce the same normalized
model and diagnostics as Stage0. Keep the modules focused instead of adding SDK
logic to an already large CLI, package-image, or module-resolution file.

Release/build integration points:

- `.github/workflows/release.yml`
- `scripts/package-release.ps1`
- `scripts/smoke-release-archive.ps1`
- a new target-specific SDK assembly script or tool
- official vendor package build scripts
- `docs/Self-host-Prep/28-release-archive-layout.md`
- user-facing install and bundled-library documentation

## 5. Work Items

### 5.0 Discovery and contract

- [x] Reproduce external-project failure to resolve `Vendor.Raylib`.
- [x] Prove the existing macOS Raylib package/native payload can compile and
  link when its package directory is supplied explicitly.
- [x] Audit Stage0 bundled-library root discovery and development launcher
  generation.
- [x] Audit release workflow, archive assembly, and archive smoke coverage.
- [x] Audit target-name agreement and clean-checkout native artifact presence.
- [x] Inspect official vendor package module ownership and native metadata.
- [x] Write this implementation strategy and acceptance contract.

### 5.1 Contract tests before resolver changes

- [x] Add a Stage0 integration fixture that models an installed SDK root with
  `sdk.json`, System, and one synthetic native-backed `Vendor.*` package.
- [x] Prove project mode resolves the installed SDK with `STARK_PATH` cleared.
- [x] Prove direct-file mode resolves the same installed SDK automatically.
- [x] Prove moving the SDK root leaves package and native paths valid.
- [x] Prove a project cannot shadow reserved SDK modules accidentally.
- [x] Add malformed schema, missing artifact, checksum mismatch, duplicate
  module, unsupported target, and incompatible package diagnostics.

### 5.2 SDK root and manifest model

- [~] Implement canonical executable-path SDK-root discovery on Linux, macOS,
  and Windows.
- [x] Add `--sdk-root` and `STARK_SDK_ROOT` override precedence.
- [x] Add a development SDK manifest and make the generated Stage0 repository
  launcher select the repository SDK explicitly.
- [x] Add Stage0 SDK manifest models and deterministic JSON decoding.
- [x] Validate schema/compiler/package-format compatibility.
- [x] Validate that every release path remains within the canonical SDK root.
- [x] Include SDK manifest identity in incremental build stamps and dependency
  LLVM cache keys.

### 5.3 Target identity and compatibility

- [x] Define the structured `SdkTargetDescriptor` and stable SDK target IDs.
- [~] Make target detection, project output paths, package builders, SDK
  assembly, `doctor`, and release scripts consume the same descriptor.
- [x] Replace exact raw-triple package selection with structured ABI
  compatibility while retaining full LLVM triple/data-layout validation.
- [x] Add CPU baseline/required-feature compatibility rules.
- [x] Add macOS deployment-minimum compatibility rules.
- [x] Add Stage0 unit and integration coverage for accepted aliases and
  rejected ABI differences.

### 5.4 Indexed SDK package resolution

- [x] Implement deterministic module-to-package lookup from `sdk.json`.
- [x] Integrate SDK lookup into the compiler-core module resolver.
- [x] Use the same SDK resolver for direct-file and project commands.
- [x] Replace string-valued bundled search-path state with typed artifact-kind
  and provenance data.
- [x] Remove implicit project-ancestor installed-SDK discovery.
- [x] Preserve explicit stage and development SDK behavior.
- [x] Make package checksum/integrity validation lazy per selected package
  while validating manifest structure eagerly.
- [x] Add resolver startup/package-selection benchmarks.

### 5.5 Package ownership and dependency metadata

- [x] Extend package images with explicit package dependency identity and
  API/content hashes where the current format is insufficient.
- [x] Make Stage0 package emission include only source-owned modules.
- [ ] Verify Stage1 package emission follows the same ownership rule.
- [x] Reject duplicate module ownership while assembling an SDK.
- [x] Resolve package dependency graphs deterministically and detect cycles.
- [x] Link only imported package graphs and preserve archive/native link order.
- [x] Add from-source versus package-image typed-fact and LLVM-fact parity
  tests for System plus a native-backed vendor package.

### 5.6 Official vendor build pipeline

- [x] Define the advertised official vendor package/platform matrix.
- [x] Create one clean target-specific SDK assembly entrypoint.
- [~] Build System and every advertised vendor package for the compiler-produced
  target descriptor and release profile.
- [x] Acquire or build pinned Raylib inputs with recorded version, checksum,
  provenance, and license.
- [~] Produce complete Raylib packages for Linux x64, Windows x64, and macOS
  arm64 before advertising those release targets.
- [x] Remove reliance on ignored or workstation-generated native archives.
- [x] Ensure official release package metadata contains no unresolved
  `pkg-config` or machine-local native paths.
- [x] Validate every referenced native library, header, runtime file, and
  license before archive creation.
- [x] Generate deterministic `sdk.json` from the staged artifacts.

### 5.7 Doctor and diagnostics

- [x] Add SDK-root, SDK-version, target, and package-integrity reporting to
  Stage0 `stark doctor`.
- [x] Add `stark doctor --strict` and machine-readable output.
- [x] Add precise official-import failure categories.
- [~] Report unavoidable host platform runtime/SDK requirements separately
  from missing Stark SDK artifacts.
- [x] Stop suggesting `STARK_PATH` for missing official SDK modules.

### 5.8 Release assembly and smoke gates

- [x] Make release jobs build a clean SDK staging directory rather than copy
  the repository vendor tree.
- [x] Package only artifacts for the release SDK target.
- [x] Generate and checksum `sdk.json` and all required package/native files.
- [x] Update the accepted release archive layout document.
- [~] Extract each archive outside the checkout with Stark overrides cleared.
- [~] Put only the extracted SDK's `bin` directory on `PATH` during smoke
  tests.
- [~] Build a fresh external `Stark.toml` project importing System without
  `-I`.
- [~] Build and link a fresh external project importing `Vendor.Raylib`
  without `-I`, dependencies, `STARK_PATH`, or `pkg-config`.
- [~] Verify the Raylib archive and required platform libraries/frameworks
  reached the linker without opening a graphical window in headless CI.
- [x] Move the extracted SDK and repeat both builds.
- [x] Corrupt/remove a required native artifact and verify `doctor --strict`
  and the build produce the intended SDK-integrity diagnostic.
- [x] Rebuild the macOS arm64 archive with `bin/stark`, then rerun the complete
  doctor, System/Raylib build-link, native-tool isolation, move, and rebuild
  matrix against that archive.

### 5.9 Stage1 parity and release adoption

- [~] Port SDK manifest models and JSON loading to `selfhost/Compiler/Sdk`.
- [~] Port canonical SDK-root discovery and target compatibility.
- [~] Port indexed module/package resolution and native metadata consumption.
- [~] Port doctor/integrity diagnostics.
- [x] Add Stage0/Stage1 normalized SDK-model and diagnostic goldens.
- [~] Run the same relocatable System/Raylib SDK fixture under both stages.
- [~] Include SDK identity in Stage1 incremental/cache keys.
- [ ] Require the SDK smoke matrix before selecting Stage1 for a release.

### 5.10 Documentation and cleanup

- [x] Document the PATH-only installation flow in durable user-facing docs.
- [x] Document official `System.*`/`Vendor.*` resolution separately from
  ordinary project dependencies.
- [x] Document SDK development overrides for compiler contributors.
- [x] Remove obsolete ancestor-directory search and misleading diagnostics.
- [x] Reconcile `ToolchainPackagingRoadmap.md` completion states with the new
  release gates.
- [~] After self-hosting is complete, migrate the durable SDK contract to
  `docs/Userfacing` and `docs/Internals` and delete this short-lived Phase 31
  tracker. Deletion is the final cleanup action and must not happen while any
  checkbox or verification-matrix requirement remains open or partial.

## 6. Required Verification Matrix

| Scenario | Stage0 | Stage1 | Release archive | Required result |
| --- | --- | --- | --- | --- |
| Project imports System | required | required | required | Builds with PATH-only SDK discovery |
| Direct file imports System | required | required | required | Builds without `-I` |
| Project imports Vendor.Raylib | required | required | required | Package and native facts resolve automatically |
| SDK directory is moved | required | required | required | Build remains byte/path independent |
| `STARK_PATH` is empty | required | required | required | No behavior change |
| Package target is ABI-incompatible | required | required | required | Precise compatibility diagnostic |
| Native archive is missing | required | required | required | `doctor --strict` and build fail clearly |
| Duplicate SDK module owner | required | required | assembly gate | SDK assembly rejects it |
| Package versus source backend facts | required | required | qualification gate | LLVM-visible facts remain equivalent |
| Unused vendor packages exist | required | required | required | They are not loaded or linked |

Release smoke runs with no repository path, no user config, and all Stark
environment overrides cleared. A graphical window does not need to execute in
CI; compilation and a real native link are mandatory.

## 7. Recommended Landing Sequence

Each slice should remain reviewable and keep existing compiler development
work usable.

1. **Contract fixture:** add failing SDK-root/direct/project/relocation tests.
2. **Stage0 root model:** add `SdkRootResolver`, manifest loading, launcher
   override, and doctor root reporting.
3. **Indexed resolver:** use the SDK manifest in compiler-core module and native
   resolution; remove string-state decisions.
4. **Target descriptor:** unify target IDs and package compatibility.
5. **Package ownership:** add dependency identities and stop embedding
   transitive System modules in vendor packages.
6. **Clean SDK assembler:** build System, Raylib, other advertised vendor
   packages, native payloads, and `sdk.json` from pinned inputs.
7. **Archive gates:** make PATH-only external System/Raylib builds and
   relocation tests release-blocking.
8. **Stage1 port:** reproduce the same manifest model, resolver, diagnostics,
   cache identity, and smoke suite.
9. **Adoption:** select Stage1 only after cross-stage SDK and performance gates
   pass.

## 8. Risks And Guardrails

- **Silent source fallback:** a release missing a binary package must fail as
  an incomplete SDK, not invoke local build tools.
- **Target over-normalization:** architecture aliases may normalize, but ABI,
  data-layout, C-model, deployment-minimum, and required-feature differences
  remain hard compatibility boundaries.
- **Backend fact loss:** SDK indexing must not reconstruct a reduced interface
  from `sdk.json`; it locates the full package image and validates identity.
- **Startup regression:** use the module index and lazy artifact verification;
  do not recursively inspect every package on every command.
- **Link bloat:** resolve and link only the imported package graph, retaining
  dead stripping and LTO.
- **Cross-stage drift:** keep normalized model/diagnostic goldens and run the
  identical archive fixture under Stage0 and Stage1.
- **Dirty-checkout releases:** assemble into a clean target directory and fail
  if any runtime artifact is sourced implicitly from ignored workspace state.
- **Platform prerequisites:** distinguish redistributable SDK payload from
  unavoidable OS SDK/runtime requirements and test the diagnostic boundary.

## 9. Progress Log

### 2026-07-13 - Audit and plan

- Reproduced external project failure for `Vendor.Raylib`.
- Confirmed the current macOS Raylib package links successfully when supplied
  explicitly, proving the binding/native metadata path itself is viable.
- Identified launcher-root, release-build, target-identity, clean-checkout
  artifact, smoke-test, package-ownership, and string-state resolver gaps.
- Established the PATH-only relocatable SDK contract, `sdk.json` architecture,
  implementation slices, and release/Stage1 gates in this document.

### 2026-07-13 - Stage0 SDK model and compiler integration

- Added focused Stage0 SDK root, strict manifest, package index, integrity,
  structured target, target-compatibility, activation, and reserved-namespace
  components under `src/Compiler/Sdk/`.
- Added canonical `--sdk-root` / `STARK_SDK_ROOT` / executable-relative
  precedence. At this point in the historical implementation sequence,
  automatic activation remained intentionally optional for a development
  compiler without an executable-relative `sdk.json`; the final hard
  SDK-not-found behavior still depended on landing the development/stage SDK
  manifests.
- Composed indexed SDK lookup with ordinary module resolution for both direct
  and project commands. Active SDK ownership now prevents project source,
  `-I`, and `STARK_PATH` from shadowing `System.*` or `Vendor.*`; a missing
  advertised official module reports `STK7495`.
- Added structured active-target and package-target validation for architecture
  aliases, OS/ABI, pointer width, endianness, exact data layout, C data model,
  relocation/code models, CPU/features, and deployment minimums while keeping
  the package image's full backend facts authoritative.
- Added SDK root and `sdk.json` identity to project incremental stamps and
  forwarded the selected root through nested project compiler invocations.
  Dependency LLVM cache identity and removal of all legacy ancestor heuristics
  remain open.

### 2026-07-13 - Stage0 resolver, link, and integrity hardening

- Made normal SDK package loading lazy and exact-module driven while retaining
  eager all-package validation for `doctor` and release assembly. An unused
  corrupt vendor package no longer blocks an unrelated System build; selecting
  that package reports its exact integrity diagnostic and cannot fall back to
  local source.
- Reserved active release/stage SDK roots as well as imports: ordinary source
  cannot declare `System*` or `Vendor*` roots, and development SDK exceptions
  are limited to declared development source roots. SDK link selection now
  requires package-image/library provenance rather than a coincidental module
  name.
- Included the target dispatch template in the owned System package, prevented
  release SDK source/template shadowing, and filtered the emitted static
  archive through the same package-ownership set as the package image.
- Added deterministic dependent-before-dependency SDK archive ordering and
  selected-package runtime-file staging with executable-relative runtime search
  paths. Target-aware runtime basename collisions now fail with `STK7476`,
  while byte-identical/hash-identical payloads deduplicate deterministically.
  General cross-package native argument ordering and platform runtime
  qualification remain performance/correctness gates.
- Made release image/library hashes mandatory and added deterministic native
  file hashes for archives, headers/sources, runtime files, and licenses.
  Selected builds and `doctor` now detect tampering with `STK7475`.
- Made SDK/package identity claims authoritative: the loader now verifies the
  embedded root module, dev/release profile, library filename, and exact binary
  package format against `sdk.json`; the fixed `sdk.json` path itself cannot be
  a child symlink. Stage0 exposes the stable compatibility line
  `stark-sdk-v1`, and release assembly queries the staged compiler for that
  value instead of copying the marketing version.
- Reject child symlink/reparse-point traversal for every indexed SDK path,
  thin static archives (including universal-archive slices), absent target data
  layouts, surrounding path whitespace, machine-local native values, and
  unsafe release staging/archive destinations. Post-activation path swaps now
  produce artifact-specific diagnostics instead of throwing from lazy loading.
- Defined directional build-profile compatibility: release packages are valid
  inputs to dev and release consumers; dev packages remain dev-only. Release
  assembly now requires actual release package metadata instead of defaulting a
  missing profile.
- Active target-specific SDK defaults now feed check/MIR/SSA/package and native
  modes, preserving triple, data layout, CPU/features, relocation/code model,
  C model, and aggregate-layout facts in emitted packages. Ordered target
  feature switches are preserved and malformed/repeated switches rejected;
  SDK packages built for generic CPUs/feature subsets remain usable by safely
  tuned applications while ordinary package compatibility stays strict.

### 2026-07-13 - Package ownership and release assembly

- Replaced presentation-string decisions in the project driver with typed
  bundled-artifact state, including target-scoped package-image recognition and
  package precedence over native source fallback.
- Changed Stage0 package emission to keep co-located modules only when they are
  owned by the package source root. `Vendor.Raylib` no longer embeds transitive
  `System.*` or sibling vendor modules; ordinary flat multi-module packages are
  retained.
- Added clean release SDK assembly that inspects staged package images,
  validates exact module ownership, dependencies, target/native facts,
  relocatable paths, payload presence, hashes, and duplicate owners, then emits
  deterministic array-based `sdk.json` and runs Stage0 `doctor` against it.
- Added pinned Raylib 6.0 release inputs for Linux x64, Windows x64, and macOS
  arm64 with size/SHA-256 verification, license/provenance staging, release
  package generation, and rejection of unresolved `pkg-config` or machine-local
  paths. The workflow now obtains LLVM before building System and Raylib and
  passes only the clean target vendor staging root to archive assembly.
- Updated archive smoke logic to clear Stark overrides, invoke the extracted
  compiler through `PATH`, build/run an external System project without `-I`,
  and build/link (but not execute) an external `Vendor.Raylib` project. The
  three-platform archive matrix has not yet been observed green, so those smoke
  items remain partial.
- Both pre-publication smoke layers now isolate `PATH` to the extracted SDK and
  bundled toolchain, and GitHub publication depends on the downloaded-archive
  smoke. The smoke moves the extracted SDK and performs fresh System/Raylib
  builds again, so incremental outputs cannot hide absolute-path leaks.
- Hardened LLVM acquisition as another release input: supported asset IDs,
  manifest-relative filenames, cache/output containment, reparse points,
  ownership markers, unique work/stage directories, and immediate pre-delete
  revalidation now guard every recursive replacement.

### 2026-07-13 - Validation evidence for the current Stage0 slice

- `dotnet build src/compiler.csproj --no-restore` completed successfully; only
  pre-existing nullable warnings in type checking were reported.
- Focused `compiler.Tests` coverage for `SdkManifestAndResolverTests` and
  `PackageImageOwnershipTests` passed, covering root precedence/canonicalization,
  strict schema and safe paths, deterministic indexes, checksums/native payload,
  duplicate owners/dependency cycles, ownership boundaries, and structured
  target compatibility.
- Focused `compiler.IntegrationTests` coverage for `CompilerCliSdkTests`,
  `ProjectCliSdkRootTests`, `ProjectCliBundledPackagePrecedenceTests`, and
  `PackageImageOwnershipIntegrationTests` passed. The native SDK fixture moved
  its SDK root, built in project and direct modes, preserved native source/link
  facts, and produced an executable returning 42.
- A disposable PowerShell 7.6.3 run exercised full SDK packaging, `doctor`,
  executable-relative automatic SDK discovery, synthetic vendor inclusion,
  legacy vendor exclusion, and byte-for-byte deterministic `sdk.json` output.
  The pinned macOS Raylib preparation path also generated and inspected a
  release package containing only `Vendor.Raylib*` modules with relative native
  facts. Linux and Windows package/archive runs remain release-gate work.
- A real macOS arm64 release build produced System and pinned Raylib 6.0,
  assembled two packages with 38 unique module owners (including both platform
  modules) and nine checksummed Raylib payload files, packaged a `.tar.gz`, and
  passed the complete archive smoke. Default dev project builds consumed the
  release SDK, Raylib linked without `-I`, dependencies, `STARK_PATH`, or
  `pkg-config`, and fresh System/Raylib builds passed again after the extracted
  SDK directory was moved.
- The final staged compiler reported `stark-sdk-v1`; the assembled manifest
  recorded that compatibility line and package format 2. With no explicit
  `--target`, an external Raylib executable linked successfully and
  `--emit-pkg` preserved the SDK triple, nonempty data layout, relocation model,
  LP64 C model, and aggregate pointer layout.
- The final consolidated focused run passed 89 SDK/target/package-ownership unit
  tests and 43 SDK/package/project integration tests with zero failures; the
  incremental compiler build completed with zero warnings and errors. The final
  self-contained Release publish also succeeded and repeated the two known
  nullable warnings in `TypeChecking.cs`. A broader compiler unit run passed
  1,857 tests and exposed 13 existing backend/package/comptime/benchmark failures
  outside this SDK slice; those failures remain open and are not reclassified as
  SDK successes.

### 2026-07-13 - Stage0 strict and machine-readable doctor diagnostics

- Added `stark doctor --strict`; ordinary doctor remains an informational
  command with exit code zero, while strict mode exits nonzero when the
  normalized report has required-capability warnings or an invalid SDK.
- Added deterministic `stark doctor --format json` schema version 1 output.
  The report contains normalized compiler/runtime/target facts, resolved
  toolchain provenance, platform-SDK and library status, SDK-root provenance,
  the full SDK target descriptor, and stable package/artifact/diagnostic arrays.
- Added per-package image, archive, native artifact, include/library directory,
  runtime file, license, and checksum status. SDK diagnostics retain their
  `STK` code and exact path and now carry a machine category and package ID;
  native checksum failures invalidate the owning payload category as well as
  the checksum category.
- Split doctor collection/rendering out of the large `CompilerCli.cs` driver
  into `src/Compiler/Doctor/CompilerDoctor.cs` and SDK-specific normalization
  into `src/Compiler/Sdk/SdkDiagnostics.cs`.
- Extended the relocatable native SDK integration fixture to corrupt and then
  remove a required runtime payload. Both selected builds and strict doctor
  fail precisely (`STK7475` / `native-file-checksum` for corruption and
  `STK7473` / `native-runtime-file` for removal); two identical JSON runs are
  asserted byte-for-byte deterministic. Focused doctor/SDK integration tests
  passed 12/12, SDK manifest/path unit tests passed 77/77, and the aggregate
  SDK/package integration slice passed 45/45 after the compiler build completed
  successfully.

### 2026-07-13 - Explicit development SDK, cache identity, and durable docs

- Added a deterministic source-only development SDK manifest writer and made
  the generated repository launcher select it without overriding a caller's
  explicit `STARK_SDK_ROOT`. The manifest declares only `stdlib/src`,
  `stdlib/templates`, and `vendor/src`; development manifests may be
  source-only while release/stage manifests still require indexed packages.
- Removed application-ancestor and unmanifested compiler-parent
  `stdlib`/`vendor` probing from project mode. Stage/build-local inputs remain
  explicit, and package/source paths used for native metadata and incremental
  stamping now come only from the selected SDK manifest.
- Extended dependency LLVM cache keys with the canonical SDK manifest path and
  content hash. Project stamps already carry the root/hash and now also stamp
  manifest-declared development/package inputs.
- Added durable user installation and contributor SDK-contract documentation,
  corrected the book/README/project guidance to the PATH-only model, updated
  the accepted archive layout with `sdk.json` and target-scoped package paths,
  and reconciled the packaging roadmap with the manifest resolver.
- Verification: the compiler built with zero warnings/errors; focused
  manifest/cache units passed 78/78; project SDK, bundled precedence, and
  project-driver integrations passed 43/43. With both `STARK_SDK_ROOT` and
  `STARK_PATH` cleared, the generated `./stark` launcher successfully checked
  `examples/standard-library/StandardLibrary.stark` through the development
  SDK manifest.

### 2026-07-13 - Stage0 package identity, indexed benchmark, and backend parity

- Extended binary package images with a deterministic identity envelope:
  package ID, API SHA-256, content SHA-256, and an ordinally sorted list of
  direct dependency IDs plus their exact API/content hashes. API identity
  covers published typed/compiler/generic facts and dependency APIs; content
  identity additionally covers target, profile, native, library, module, and
  complete dependency content facts.
- Package loading now rejects malformed identity envelopes with `STK7137` and
  facts changed after hashing with `STK7138`. SDK loading compares the indexed
  descriptor and exact dependency closure with the package image (`STK7458` /
  `STK7459`). Release assembly requires these identities, verifies every
  dependency against the selected package, and emits the hashes into
  deterministic `sdk.json`.
- Corrected the official Raylib release build to disable developer
  `STARK_PATH` input while consuming the staged System package. This prevents
  repository System source from silently shadowing the release package and
  ensures `Vendor.Raylib` records the exact selected System identity.
- Added an indexed startup/selection regression guard using 256 packages,
  4,096 modules, and 524,288 lookups. It asserts zero package materialization
  during startup/selection, bounded allocation, and bounded runtime. A direct
  comparison against the retired directory-scan path remains open.
- Added source-versus-package LLVM parity coverage for a native-backed vendor
  FFI package. The package path produces byte-identical LLVM to the source
  path while preserving the typed FFI signature, `LinkName`, native library,
  native linker argument, and package identity. Real-System coverage was added
  by the subsequent selected-link-graph/backend-evidence slice below.
- Focused validation passed 79 identity/manifest/resolver tests and the native
  parity integration test. A real macOS arm64 run built the release System and
  pinned Raylib packages, assembled two identities and 38 unique module
  owners, passed `doctor` package integrity/target validation, and reproduced
  byte-identical `sdk.json` on a second assembly. The external `SimpleGame`
  probe now resolves `Vendor.Raylib`; its remaining diagnostics are ordinary
  source errors (the five-argument `DrawText` signature and a missing return),
  not SDK/package resolution failures.

### 2026-07-13 - Stage1 owned SDK manifest model and strict loading

- Added focused `ManifestModel.stark` and `ManifestLoading.stark` modules under
  `selfhost/Compiler/Sdk/` and exported them through the Stage1 SDK surface.
  The model owns every decoded string and retains manifest-order modules,
  packages, dependencies, native metadata, and checksums in contiguous arrays
  ready for the later package/module index.
- Added case-sensitive JSON text and file loading with strict object-member
  masks. Missing, duplicate, unknown, or wrongly typed members are rejected;
  the loader requires schema 1, exact `stark-sdk-v1` compiler compatibility,
  and package format 2. Target decoding preserves the complete LLVM/ABI input
  set: triple, data layout, architecture/OS/ABI, pointer width, endianness,
  baseline CPU/features, relocation/code/C models, and minimum OS.
- Added forward `System.Json` child/sibling iteration and used it throughout
  the loader. Large module/package arrays are decoded in one forward walk of
  the JSON tree instead of the quadratic repeated-ordinal scan performed by
  `TryChildAt`. Direct checks of `System.Json` and its focused forward-sibling
  fact passed (the module retains its four pre-existing recursive-parser
  warnings).
- Added Stark-native facts for a Stage0-shaped manifest, native/dependency fact
  retention, malformed/unknown/duplicate rejection, compatibility rejection,
  and repository `sdk.json` file loading. Direct Stark ownership/lifetime
  checks passed for `ManifestModel.stark`, `ManifestLoading.stark`, and the
  expanded `SdkTests.stark`. Runtime execution of the new facts is still open,
  so this work item remains partial rather than complete.

### 2026-07-13 - Stage1 target compatibility follow-up

- Replaced raw architecture/OS/ABI string equality with Stage0-compatible,
  ASCII-safe normalization: `arm64`/`aarch64`, `amd64`/`x86_64`, and
  `macos`/`macosx`/`darwin` aliases are accepted case-insensitively with outer
  whitespace, while unknown near-matches remain incompatible. Non-Darwin ABI
  names are compared case-insensitively after trimming rather than weakened to
  a broad family match.
- Added bounded two-to-four-component numeric deployment-version parsing.
  Active targets must have a minimum equal to or newer than the SDK minimum,
  including minimums recovered from `macos[x]` LLVM triples when the explicit
  field is absent. Malformed or older active claims remain incompatible.
- Exact LLVM data layout, C data model, relocation/code model, pointer width,
  endianness, CPU, and required-feature comparisons remain in the compatibility
  path. Focused alias, exact-backend-fact, and directional-version Stark facts
  were added; direct checks of `TargetCompatibility.stark` and the expanded
  `SdkTests.stark` passed. Runtime execution remains open with the combined
  root/compatibility work item partial.

### 2026-07-13 - Selected link graph and real System backend evidence

- Extended the Stage0 SDK link plan with the exact ordered package-image set
  selected by the imported package graph. Executable native-dependency
  collection now consumes that same dependent-before-dependency order after
  ordinary source imports, so graph dependencies contribute native facts even
  when no semantic module from that dependency had to be materialized. SDK
  packages outside the selected graph are never opened, validated, or linked.
- Added a real `System.BitOperations` release-library/package gate. The source
  and package paths select the same typed `PopCount` overload and call-argument
  facts and emit byte-identical `main` and `PopCount` implementation bodies,
  including `llvm.ctpop.i32`. Published package compiler facts also reach LLVM:
  they safely strengthen the conservative source-import view to `memory(none)`
  for `PopCount` and `norecurse` for the consumer.
- Added a selected-SDK-graph executable gate with used System/vendor packages
  and a deliberately missing unused vendor package. The build succeeds without
  touching the unused image or archive, links used vendor before System, keeps
  native libraries and linker arguments in the same order, and preserves
  `-flto=thin`, `-O3`, LLD selection, function/data sections, and the platform
  dead-strip option.
- Audited Stage1 ownership rather than marking it complete prematurely.
  `LogicalPackageManifestJsonBuilder.AddModule` is still a low-level builder
  that accepts caller-selected names; Stage1 does not yet have the compiler
  package-emission orchestration/source-root ownership selection needed for a
  genuine parity test. The Stage1 ownership checkbox therefore remains open.
- Focused validation passed 76/76 SDK manifest/resolver units and all 3
  `PackageImageSdkParityIntegrationTests` integrations.

### 2026-07-13 - Cross-stage normalized manifest and diagnostic goldens

- Added one checked-in, fully populated development SDK manifest shared by the
  C# and Stark-native tests. It covers every header/target field, baseline
  features, module ownership, two packages, exact dependency identities,
  package checksums, all native metadata lists, native file checksums, link
  arguments, and development source roots. A checked-in line-oriented summary
  is the common expected model; it is explicitly a compatibility/debugging
  surface rather than a persisted or cryptographic identity format.
- Added matching Stage0 and Stage1 normalized-summary renderers. The C# golden
  loads the shared JSON through `SdkManifestLoader`; the Stark facts load that
  same file through `LoadSdkManifestFile` and compare the same summary. The
  Stark implementation uses overlap-safe text appends and direct `retborrow`
  access, preserving strict ownership rather than relying on unsafe alias
  assumptions.
- Added shared malformed-JSON and incompatible-schema fixtures. They exposed
  that Stage1's manifest diagnostic mapping was shifted from Stage0; Stage1 now
  maps an unreadable manifest to `STK7400`, malformed JSON to `STK7401`, and an
  incompatible schema to `STK7402`, matching Stage0.
- All 4 C# golden tests passed. Direct Stage0 checks of both
  `SdkGoldenTests.stark` and the existing `SdkTests.stark` facade passed. A
  filtered Stark runtime attempt was capped after roughly two silent minutes
  in project build/setup before the test runner started, so cross-stage runtime
  execution remains tracked by the separate relocatable-fixture checkbox.

### 2026-07-13 - Stage1 package index and integrity boundary

- Added a compact Stage1 `SdkPackageIndex`: package and module names are copied
  once into hash tables, exact case-sensitive lookup accepts borrowed ASCII
  without allocating a temporary key, and values remain manifest-order package
  indices. Index construction retains the first declaration deterministically
  and reports duplicate package IDs/module owners, missing module owners,
  missing/self dependencies, and dependency cycles in manifest order. Cycle
  discovery is iterative so SDK size does not become a call-stack limit.
- Added Stage1 SDK path and payload validation. Paths must be nonempty,
  whitespace-clean, forward-slash relative paths without empty, `.` or `..`
  segments; normalized results must remain under the canonical SDK root, and
  every existing child symlink/reparse component is rejected. Package images,
  libraries, native artifacts/include and library directories/runtime and
  license files retain their distinct Stage0 diagnostic categories. Native
  checksum paths must name a declared native file.
- Added canonical lowercase SHA-256 text validation and an exact
  expected-versus-observed digest boundary. The reusable streaming provider
  and file-byte integration described below now feed this boundary directly.
  Bulk payload validation canonicalizes the SDK root once to avoid repeating
  that work for every manifest path, while a separate selected-package entry
  point avoids inspecting unused vendor payloads.
- Exported both modules from `Compiler.Sdk`, aligned each added semantic and
  integrity diagnostic with its Stage0 assignment (spanning `STK7410` through
  `STK7475`), and added focused Stark facts for exact lookup,
  duplicate/missing/cyclic graphs, portable path rejection, typed
  native-payload absence, and checksum comparison. Direct checks passed for
  `PackageIndex.stark`,
  `IntegrityValidation.stark`, and the combined `SdkTests.stark` root. A
  six-fact runtime attempt was capped after 240 seconds in the silent project
  build phase while another cold compiler test occupied the machine, so runtime
  execution remains uncredited.
- The two Phase 31 items remain partial: model-level consumers now use the
  index, but Stage1 doctor/compiler-driver integration, native linker command
  construction, remaining cross-stage diagnostic/runtime parity, cache-key
  consumption, and relocatable System/Raylib smoke coverage are still open.

### 2026-07-13 - Stage1 owned SDK activation facade

- Added an owned Stage1 activation session that composes explicit root
  precedence, `sdk.json` loading, exact target/backend compatibility, package
  indexing, and an explicit payload-integrity policy. The session owns the
  root, decoded manifest, and allocation-once index; lookup values remain
  manifest-order indices and callers borrow package/native/LLVM facts directly
  from the manifest rather than receiving a copied or flattened activation
  model.
- Normal compiler activation selects `Deferred` payload validation so an
  unused bundled vendor package causes no startup filesystem scan. Selected
  package/module indices can be passed through the session's one-package
  integrity boundary. `EagerAllPackages` is explicit doctor/release behavior
  and walks every declared payload only when requested.
- Target rejection happens before index or payload work and retains exact data
  layout, pointer width, endianness, CPU/features, relocation/code model, C
  model, and deployment-minimum comparisons. The activation error preserves
  structured SDK diagnostics separately from allocation/layout failures.
- Added four focused Stark facts covering retained System/Raylib and backend
  facts through manifest indices, lazy selected-vendor integrity, eager
  all-package path validation, exact target rejection, and explicit-root
  precedence. Direct checks passed for `Activation.stark`,
  `SdkActivationTests.stark`, the existing `SdkTests.stark`, and
  `SdkGoldenTests.stark`.
- This does not claim Stage1 compiler-driver/CLI activation, native link-plan
  consumption, or runtime execution of this activation project. File-content
  SHA-256 is now supplied by the focused streaming slice below.

### 2026-07-13 - Stage1 selected package link-plan model

- Added a model-level `SdkPackageLinkPlan` over `SdkPackageIndex`. Exact
  imported module names select only their owning SDK packages; ordinary source
  or project imports that do not appear in the SDK index are ignored. The
  selected set closes over exact package identities without scanning or
  validating unrelated package payloads.
- The plan returns manifest package indices rather than copied descriptors, so
  package images, compiled libraries, native artifacts/directories/runtime
  files, ordered native libraries/link arguments, and all target/backend facts
  remain available from the original owned manifest. A deterministic lexical
  Kahn traversal orders each dependent before its dependencies for the
  left-to-right static-link boundary and uses an iterative heap rather than
  recursion.
- Missing selected dependencies, selected self-dependencies, invalid selected
  module owners, and selected cycles reuse the existing structured SDK
  diagnostics and leave no partial plan. Focused facts cover a used
  `Vendor.Raylib` package preceding `System`, exact case-sensitive selection,
  an unavailable and checksum-malformed unused vendor package remaining outside
  both the plan and selected-payload validation, selected missing/cyclic graph
  rejection, and preservation of manifest-order native libraries and link
  arguments.
- Direct checks passed for `LinkPlan.stark` and the combined `SdkTests.stark`
  root. Runtime execution remains uncredited while the unrelated cold compiler
  test occupies the machine.
- Phase 5.9 remains partial. This slice does **not** claim compiler
  loaded-module provenance wiring, driver/CLI activation, native linker command
  construction, cache-key consumption, or relocatable System/Raylib smoke
  parity.

### 2026-07-13 - Stage1 deterministic SDK cache identity boundary

- Audited the self-host tree and confirmed there is not yet a Stage1 project
  driver, incremental build stamp, dependency-LLVM cache, or cache storage
  consumer to wire. The current self-host package exposes compiler passes and
  model-level SDK activation/link planning; the differential driver is a test
  tool, not a build-cache owner. This slice therefore does not claim CLI or
  incremental-cache integration.
- Added a versioned, owned `SdkCacheIdentity` API. It streams a length-framed
  SHA-256 encoding rather than materializing the normalized diagnostic summary,
  and combines the raw `sdk.json` digest with schema/compatibility facts, the
  full LLVM target contract (triple, data layout, CPU/features, relocation/code
  model, C model, deployment minimum, pointer width, and endianness), and the
  exact selected package graph. Selected package identity/checksum,
  dependency, path, ordered native-library/link-argument, and native-file
  checksum facts all participate. The SDK root path deliberately does not, so
  relocation of byte-identical contents preserves the identity.
- Added focused Stark facts with a fixed digest golden, deterministic repeat
  construction, manifest-digest invalidation, selected-graph invalidation,
  explicit data-layout invalidation even under a deliberately reused manifest
  digest, and rejection of package-plan indices from a different manifest.
  Direct checks passed for `CacheIdentity.stark`,
  `SdkCacheIdentityTests.stark`, the exported `Compiler.Sdk` facade, and the
  existing combined `SdkTests.stark` root.
- The Phase 5.9 cache item is partial: its deterministic input contract is now
  owned and tested, but a future Stage1 driver must feed it the manifest-file
  digest and selected package plan, then compose the result into project and
  dependency-emission keys before the checkbox can be complete.

### 2026-07-13 - Stage1 streaming SHA-256 payload verification

- Added reusable `System.Cryptography.Sha256` state/digest APIs with fixed
  32-bit state, a fixed 64-word schedule, explicit wrapping arithmetic, direct
  processing of complete input blocks, and a bounded tail buffer. File hashing
  reads through one fixed 8 KiB stack chunk and never allocates or copies the
  whole payload. Digest-to-manifest comparison is allocation-free and retains
  canonical lowercase hexadecimal validation.
- Runtime facts passed 5/5 for the NIST empty and `abc` vectors, a 112-byte
  multi-block vector, incremental updates/finalization, and a streamed file
  fixture. The SHA module and fact source also pass direct checks.
- Wired package images, compiled libraries, and declared native files into
  streamed byte verification. The selected-package entry point hashes only the
  requested manifest index; eager doctor/release activation hashes all package
  payloads. Package image/library mismatches remain `STK7468`, native file
  mismatches remain `STK7475`, and missing files retain their more precise
  path-kind diagnostics without an extra checksum failure.
- Added a checked-in two-package fixture whose System image/archive hashes are
  correct while the unused Raylib image and native archive are deliberately
  corrupt. Focused facts prove selected System validation stays clean, selected
  Raylib reports the two exact mismatch categories, and eager validation finds
  the same corrupt bytes. Those facts and all affected SDK modules pass direct
  checks. Two Stage0 runtime attempts for the SDK-dependent projects were
  capped after 240 silent seconds before an executable or fact runner appeared,
  so their runtime execution remains uncredited.
- This completes the Stage1 file-byte hashing primitive and model-level
  selected/eager integrity behavior. Phase 5.9 remains partial until the driver
  activates the session/link plan, doctor exposes eager results, and the same
  relocatable System/Raylib archive runs under both stages.

### 2026-07-13 - Shared relocatable System/Raylib Stage1 model fixture

- Promoted the existing cross-stage normalized System/Raylib manifest into an
  integrity-valid shared model fixture by adding deterministic payload
  stand-ins at its declared package-image, compiled-library, native-archive,
  runtime-file, license, include-directory, and development-source-root paths.
  Package/native SHA-256 declarations and the shared normalized summary now
  describe those exact checked-in bytes rather than placeholders. These are
  deliberately model/integrity inputs, not executable package or native
  libraries; the driver smoke remains the gate for real artifacts.
- Added a focused Stage1 relocation fact that copies this same manifest and
  payload tree to an isolated SDK root, activates it through the owned session,
  resolves exact `System.Core` and `Vendor.Raylib` owners, closes the selected
  graph in dependent-before-dependency order, and streams every selected
  package/native checksum. It verifies the complete target/backend contract,
  package identities and dependency hashes, relative package/library paths,
  ordered native metadata, and development source-root facts.
- The fact then moves the SDK directory and repeats activation, selection,
  integrity, and cache construction at the new root. The resulting Stage1 SDK
  cache identity is identical, proving the physical root does not enter the
  content/backend/package identity. Borrowing both the manifest and its index
  from one activation session also exposed an overly strict default disjoint
  contract on link-plan construction; the API now explicitly permits that
  intended shared activation root without copying either structure.
- A short direct executable probe reached MIR lowering and exposed an existing
  unsupported nested dynamic-slice assignment shape in the link-plan heap and
  package-index cycle work arrays. Both now route already-reserved sparse
  writes through small audited direct-place setters. Direct checks passed for
  the new relocation fact, `LinkPlan.stark`, `PackageIndex.stark`, and the
  shared Stage1 golden. All 4 Stage0 cross-stage golden tests passed, and an
  independent checksum audit matched every manifest declaration to its file.
  The probe was not rerun after the lowering repair, so Stage1 runtime remains
  explicitly uncredited.
- The cross-stage relocatable-fixture item is now partial rather than open.
  The shared bytes run through Stage0's normalized loader and Stage1's complete
  model-level activation/integrity/link/cache path, but Stage1 still has no
  compiler driver consuming the session or native plan. A true same-program
  Stage0/Stage1 build-and-link smoke therefore remains required before this
  checkbox can be complete.

### 2026-07-13 - Stage1 package-owned module selection boundary

- Audited upward from `LogicalPackageManifestJsonBuilder.AddModule`. Stage1 has
  no production package-emission orchestration: no compiler code constructs
  the logical manifest builder, no loaded-module aggregate carries canonical
  source paths plus package provenance into it, and no Stage1 driver emits the
  resulting image. The remaining Phase 5.5 checkbox therefore stays open.
- Added the smallest reusable boundary supported by the current architecture.
  `LogicalPackageModuleOwnershipGraph` owns copies of canonical source paths,
  module names, and exact external package ID/API/content hashes, so it retains
  no borrow into a package loader. `LogicalPackageOwnedModuleSelection` owns
  only stable graph indices, preserving the original per-module
  source/typed/compiler/generic/backend/native carriers for direct consumption
  instead of copying or flattening them.
- Selection mirrors Stage0's macOS ownership rules: official `System` and
  `Vendor` roots use an exact namespace boundary, ordinary roots use the
  canonical root-file directory, package-backed modules are never republished,
  surface modules seed the set, and import closure retains owned private
  helpers. Module and dependency output is lexically deterministic. Imported
  package identities are deduplicated without losing their exact hashes, and
  conflicting identities fail transactionally with an empty result.
- Added a builder adapter that adds exactly the selected module shells while
  retaining the candidate-index mapping needed to attach every existing fact
  family. Focused Stark facts prove `Vendor.Raylib` retains its root and
  `Vendor.Raylib.Core` helper while excluding sibling vendor, unrelated, and
  System modules; exact System/Raymath identities survive selection; target,
  native-library/link-argument order, and compiler linkage facts survive
  manifest construction. A second fact covers ordinary flat source roots,
  private imported helpers, outside-root exclusion, and identity-conflict
  rejection. Direct checks passed for the selector and focused fact module.
- This is not end-to-end Stage1 package emission. A future driver must build
  the graph from canonical loaded-source provenance, call the selector before
  populating module rows, serialize package identity/dependency metadata (the
  current Stage1 logical manifest lacks that package-level section), and emit
  and reload a real `Vendor.Raylib.starkpkg`. Windows path canonicalization and
  casing parity also remain to be qualified when that driver exists.

### 2026-07-13 - Stage0 stage manifests and final macOS archive qualification

- Added deterministic, atomic `kind: stage` SDK manifest production through
  `--write-stage-sdk-manifest <stage-root> <stage-name>`. The writer derives
  package target, identity, dependency, module-ownership, native-link, and
  checksum facts from the actual stage package images; rejects duplicate
  owners, incompatible targets, and paths outside the stage root; and validates
  its own result through the normal loader. Project mode automatically selects
  a valid active-stage manifest below CLI/environment precedence and suppresses
  legacy raw stage scans only after that replacement exists. Development SDK
  precedence and the no-manifest bootstrap fallback remain covered.
- Package selection now accepts only structured architecture/OS/ABI/deployment
  aliases that preserve the full data layout, C model, relocation/code model,
  CPU, and feature contract. A real Linux GNU-versus-musl SDK/package
  integration fixture proves genuine ABI differences still fail with the
  compatibility diagnostic rather than reaching a later linker failure.
  Indexed resolution also has a guarded comparison against the retired
  directory scan; the focused benchmark retains at least the required 2x
  advantage.
- Completed the official-import diagnostic boundary. No active manifest emits
  `STK7496` with install/doctor guidance; an SDK that does not advertise the
  module emits `STK7495`; development source-root violations emit `STK7494`;
  manifest/compiler/package incompatibility, target incompatibility, missing
  payloads, and checksum corruption retain their more precise existing SDK
  categories. Canonical source-root comparison now resolves intermediate
  symlinks consistently, including macOS `/var` versus `/private/var` aliases.
- The release publish stamps the compiler informational version from the
  requested release version and SDK assembly rejects a mismatch. Stable SDK
  target IDs, rather than raw LLVM triples, name `stdlib/dist` and
  `vendor/dist`. The smoke gate parses strict JSON doctor output, requires both
  compiler and SDK status `ok`, and invokes the PATH command from outside the
  checkout so a repository launcher cannot shadow the extracted executable.
  In this pre-`bin/` qualification run, `-IsolatePath` placed only the
  directory containing the root-level command on `PATH`; bundled LLVM tools
  resolved from the compiler distribution, while macOS's host-owned
  `/usr/bin/xcrun` remained available without exposing `/usr/bin` as a
  fallback tool directory.
- Acquired the checksum-pinned official LLVM 22.1.8 macOS arm64 asset and
  trimmed it for the retained Stage0 textual-LLVM backend. The staged payload
  contains Clang/Clang++, LLD drivers, llvm-ar/ranlib, and Clang resources; it
  has no symlinks and records verified hardlink aliases. Physical size is
  394 MiB versus 915,495,570 logical file bytes. Release assembly restores
  those hardlinks after copying, and the tar archive records them as hardlink
  entries. The official macOS asset does not contain a loadable
  `libLLVM.dylib`; strict Stage0 doctor reports that as an informational note,
  not a missing capability. A future direct Stage1 backend archive still
  requires a reproducibly built loadable library.
- Built a version-stamped macOS arm64 candidate with two packages and 38 module
  owners. Its stage is 502 MiB and its checked archive is 153 MiB. With every
  Stark override cleared and only the command directory on `PATH`, strict JSON
  doctor returned `ok`; a fresh System project built and ran; a fresh
  `Vendor.Raylib` project resolved without `-I`, dependencies, `STARK_PATH`, or
  `pkg-config` and completed a real native link without opening a window; MIR,
  SSA, LLVM, object, library, executable, runtime, and package-owned native-C
  paths passed; then the SDK was physically moved and fresh System and Raylib
  projects linked again.
- Final focused evidence includes 106 SDK/target/cache/ownership unit tests,
  27 SDK/project/package-parity integrations, 18 vendor-binding/release audits,
  34 project CLI tests, 15 SDK-root/precedence/target integrations, and 94
  manifest/target units. The compiler build, all release-script PowerShell AST
  parses, both asset-manifest JSON parses, release-workflow YAML parse, archive
  checksum verification, and `git diff --check` also passed. Broader Stage1
  runtime projects that exceeded their bounded silent build window remain
  explicitly uncredited.

### 2026-07-13 - Conventional `bin/` release layout selected

- Replaced the root-level release command contract with the conventional
  `<sdk-root>/bin/stark[.exe]` layout. `sdk.json`, `release.json`, `stdlib/`,
  `vendor/`, `toolchain/`, licenses, and documentation stay at the SDK root;
  users add only `<sdk-root>/bin` to `PATH`.
- Kept executable-relative discovery deliberately bounded. Release commands
  select the manifest one level above `bin/` after canonicalizing symlinks; the
  root-level repository launcher may select its sibling development manifest.
  This is compatibility for local compiler development, not a second published
  archive layout, and no arbitrary parent search is permitted.
- Mirrored that bounded discovery contract in Stage1 with iterative
  component/final-symlink canonicalization and a 256-link cycle limit. Focused
  Stage1 facts cover the conventional parent, adjacent-manifest precedence,
  the incomplete-release marker, unrelated ancestors, and executable
  symlinks. Both touched Stark roots pass direct semantic checking with zero
  diagnostics. The filtered runtime attempt remains uncredited because the
  existing `OwnedModuleSelection.stark` lower-MIR invariant failed before the
  facts executed.
- Rebuilt the macOS arm64 candidate with `bin/stark`, two packages, and 38
  module owners. The clean stage is 502 MiB and the checked archive is 153 MiB.
  The complete release smoke passed with every Stark override cleared and only
  the extracted SDK's `bin` directory on `PATH`: strict JSON doctor, System and
  `Vendor.Raylib` build/link, MIR/SSA/LLVM/object/library/executable emission,
  runtime and package-owned native-C paths, physical SDK relocation, and fresh
  System/Raylib builds from the moved SDK. `release.json` records
  `paths.compiler` as `bin/stark`, no root-level compiler command is present,
  and the archive checksum verifies.

### 2026-07-13 - Explicit remaining work

- Run and repair the Linux x64 and Windows x64 archive gates on those hosts,
  including canonical executable/symlink discovery, stable target aliases,
  strict doctor host-prerequisite separation, checksum corruption/removal, and
  PATH-only System/Raylib build-link-relocation smoke. Linux additionally needs
  a deliberate sysroot/CRT policy plus qualification of Raylib's GL/X11
  runtime/link requirements; PATH isolation alone does not hide host
  development files on a feature-rich runner.
- Have each future Stage1/Stage2 bootstrap producer invoke the landed stage SDK
  manifest writer after assembling its `stdlib`/`vendor` package images. The
  legacy scan remains intentionally available only because no production
  Stage1 compiler driver exists yet.
- Build and qualify a reproducible trimmed macOS LLVM toolchain with a loadable
  `libLLVM` before selecting the direct Stage1 backend for macOS releases; do
  not relabel the official archive's static component libraries as that
  runtime capability.
- Add Stage1 compiler package-emission orchestration that derives canonical
  loaded-module provenance, feeds the owned-module selector, serializes package
  identities/dependencies, populates every selected fact family, and emits a
  reloadable image; the current boundary is model/builder-level only.
- Wire the owned Stage1 activation session into the compiler driver, consume
  selected package/native link facts, feed deterministic SDK identity into
  real project/dependency cache keys, expose eager integrity through Stage1
  doctor, and turn the shared model-level relocation fixture into an identical
  real Stage0/Stage1 System/Raylib build-and-link smoke.
- Make the complete cross-platform SDK smoke matrix a Stage1 release-selection
  gate. After those Stage1 and platform rows are green, migrate any remaining
  durable contract text and delete this short-lived tracker as the final step.
