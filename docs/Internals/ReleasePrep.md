# Public Release Preparation

Status: active, short-lived release tracker.

This document tracks the operational work required to publish Stark's Stage0
compiler as a public GitHub release. It is not a durable architecture or product
specification. Move lasting decisions into the appropriate Userfacing or
Internals document as they are made.

Delete this file after the release is published, the immediate post-release
verification is complete, and any unfinished follow-up has been moved to the
normal project tracker or GitHub issues.

## Release Identity

- Release version: undecided
- Release type: release candidate before stable release
- Release commit: undecided
- Release date: undecided
- Release compiler: C# Stage0
- GitHub repository: `AlexanderBaggett/Stark`
- Distribution: exhaustive, relocatable, target-specific SDK archives attached
  to a GitHub release
- Architecture policy: 64-bit only (`x86_64`/`x64` and `arm64`); 32-bit
  architectures are unsupported

Durable contracts:

- [Release Archive Layout](ReleaseArchiveLayout.md)
- [SDK Layout and Resolution](SdkLayoutAndResolution.md)
- [Installing the Stark SDK](../Userfacing/InstallingTheStarkSdk.md)
- [Stark Projects and Solutions](../Userfacing/ProjectsAndSolutions.md)
- [Compiler Work Tracker](../Self-host-Prep/TASKS.md)

The release coordinator should update this document with concise evidence or a
link to the relevant workflow run, issue, or commit. Detailed investigation
logs belong elsewhere.

## 0. Odin-Compatible Release Contents (Locked)

The inclusion boundary is now a product decision, not an open research topic.
Stark releases will follow Odin's compiler-SDK model: download one native-host
archive, extract it, keep the compiler beside the language libraries, satisfy
the host's documented native-development prerequisite, add the command to
`PATH`, and compile. Stark retains useful additions—`stark doctor`, an optional
installer, checksummed package ownership, signing, and an offline README—but
those additions do not change what belongs inside the compiler SDK.

“Self-contained Stark SDK” means self-contained with respect to Stark-owned and
compiler-version-sensitive files. It does not mean embedding a complete general-
purpose C/C++ development environment or redistributing an operating-system SDK.

| Component | Ship in every applicable target archive? | Contract |
|---|---:|---|
| Stage0 compiler and .NET runtime | Yes | Publish as a .NET 10 self-contained deployment; no separate .NET install. |
| Compiler-private LLVM backend payload | Yes | Ship only the exact backend programs/libraries/resources the compiler itself requires. Stage0 may use a trimmed private Clang payload while it emits textual LLVM; Stage1 uses its qualified libLLVM runtime. This is not a public LLVM SDK. |
| `System.*` | Yes | Ship source/reference content plus ABI-coherent package images and native archives for the archive target. |
| Official `Vendor.*` collection | Yes | Ship the complete target-advertised collection, including bindings/source, package images, native payloads, link facts, licenses, and examples. No per-package installation ritual. |
| Examples, offline docs, manifests, notices, checksums, and provenance | Yes | Keep them versioned with the compiler and usable without the repository. |
| Optional installer and uninstaller | Yes | A Stark convenience beyond Odin's extraction/PATH flow; the manual flow must remain first-class. |
| Complete LLVM/Clang/LLD development distribution | No | Upstream archives may be acquisition inputs, but release assembly allowlists only compiler-private runtime files. |
| Apple SDK, Windows SDK/MSVC, Linux sysroot, drivers, and base OS libraries | No | These remain the narrow, documented host-development/operating-system layer. |
| Ambient package-manager or `pkg-config` copies of official Vendor libraries | No | An advertised official package resolves only from the SDK. |

The allowed host-development layer matches Odin's practical boundary:

- Windows: a supported MSVC/Windows SDK installation. A compiler-private linker
  executable may be bundled where required, but SDK/CRT libraries still come
  from the supported Microsoft installation.
- macOS: Xcode Command Line Tools or full Xcode for the macOS SDK and platform
  linker surface.
- Linux: a supported Clang/native development environment and ordinary system
  ABI/development libraries. The compiler-private backend remains SDK-owned;
  final host linkage remains a documented platform prerequisite.

Platform graphics, audio, window-system, and driver libraries may remain normal
OS facilities. Everything owned by an advertised `Vendor.*` package—including
its Stark archive, native binaries or reproducible source payload, headers,
runtime files, license, and ordered link facts—must already be inside the SDK.

- [x] Adopt the Odin-style compiler-SDK/host-development inclusion boundary for
  all Stage0 release work and future Stage1 releases.
- [ ] Publish Stage0 as a .NET 10 self-contained deployment for each release
  RID; users must not install .NET separately.
- [ ] Define an allowlisted compiler-private backend manifest per target. Remove
  every upstream LLVM file not required for Stage0 compilation, object emission,
  archiving/link-driver operation, licensing, or diagnosis.
  - Implemented: schema-2 per-file closure, hashes/sizes, development-tree
    rejection, archive-relative packaging, and provenance for the three enabled
    rows. The acquisition manifest now also pins the official Linux arm64 and
    Windows arm64 archives, signatures, and attestations, giving five reviewed
    upstream binary inputs in total. The missing macOS x64 upstream binary is
    now covered by a pinned, repository-controlled source-build recipe.
  - Remaining: execute and qualify the real closure on the Linux arm64 and
    Windows arm64 native runners, run the complete release/archive/install path
    for macOS x64, and requalify every enabled closure from the final release
    commit.
- [x] Keep backend discovery archive-relative by default and label it as an
  internal compiler dependency in diagnostics and release metadata, not as a
  bundled general-purpose toolchain.
- [x] Separate compiler-private backend resolution from final host-link
  prerequisite discovery so `stark doctor` can report corruption and missing
  host development facilities as different failures.
  - Implemented: separate text/JSON `compilerBackend` and `hostDevelopment`
    reports and remediation, plus release-manifest resolver policy that forbids
    ambient `PATH` fallback for private Clang, LLD, and archiver components while
    retaining explicit development overrides and separate host-link discovery.
- [ ] Bundle all required `System.*` package images and native archives.
- [ ] Bundle every package advertised by the target SDK's official Vendor
  catalog, including its Stark wrapper, package image, native library/runtime,
  required source/header payload, target link facts, license, and examples.
- [ ] Bundle SDK manifests, checksums, documentation, examples, notices,
  provenance, installer, and uninstaller; make completeness machine-verifiable.
- [ ] Prohibit dependencies on repository paths, developer caches, ambient
  `STARK_*` overrides, `STARK_PATH`, or package-specific `pkg-config` setup in a
  release SDK.
- [ ] Permit the installer to detect and, with explicit user consent, invoke the
  official platform mechanism for the narrow host-development prerequisites.
  It must not download missing Stark, System, Vendor, or compiler-private backend
  payloads.
- [x] Make `stark doctor` distinguish bundled SDK integrity from missing host
  prerequisites and print the exact supported remediation for the active OS.
- [x] Keep the SDK as one relocatable directory after extraction or installation;
  do not flatten or separate package-owned native payloads from their manifests.
- [x] Treat any missing advertised package payload or any undeclared external
  package dependency as a release-blocking archive defect.
- [ ] Add clean-machine smoke tests that start without Stark, .NET, or a separate
  LLVM installation, install only the documented host-development prerequisite,
  and then prove core and Vendor builds from the extracted SDK.

Archive format is an implementation detail, but must preserve the contract.
Windows should use `.zip`; macOS and Linux should normally use `.tar.gz` because
it reliably preserves executable modes and symbolic links. If `.zip` is also
published for Unix, it needs independent tests that restore/verify executable
modes and links after extraction.

- [x] Use `.tar.gz` for Linux/macOS and `.zip` for Windows; encode and validate
  the choice in the target and archive-content manifests.
- [x] Do not publish Unix `.zip` assets in v1; retain executable modes and links
  through `.tar.gz` rather than adding a second restoration protocol.
- [x] Ensure every archive has exactly one top-level
  `stark-<version>-<os>-<arch>/` directory and deterministic filenames.

## Odin Research Findings and Stark Decisions

Research snapshot: Odin `dev-2026-07a`, its official installation guide, and
the repository's nightly workflow. Odin demonstrates a pragmatic compiler-SDK/
host-toolchain boundary rather than a zero-host-dependency SDK.

Primary references:

- [Odin Getting Started and release requirements](https://odin-lang.org/docs/install/)
- [Odin GitHub releases](https://github.com/odin-lang/Odin/releases)
- [Odin nightly workflow](https://github.com/odin-lang/Odin/blob/master/.github/workflows/nightly.yml)
- [Odin curated Vendor collection](https://github.com/odin-lang/Odin/blob/master/vendor/README.md)

| Platform | Odin release payload | Documented host prerequisite |
|---|---|---|
| Linux x64/arm64 | Statically linked Odin compiler (including LLVM), `base`, `core`, `vendor`, examples | Clang for final linking plus normal system development libraries |
| macOS x64/arm64 | Odin compiler, bundled `libLLVM` dependencies, `base`, `core`, `vendor`, examples | Xcode Command Line Tools/full Xcode |
| Windows x64 | `odin.exe`, `LLVM-C.dll`, `lld-link`, `wasm-ld`, `base`, `core`, `vendor`, examples | MSVC compiler and Windows SDK from Desktop Development with C++ |

Odin's current monthly archives are roughly 57–64 MiB compressed on Linux and
macOS and 139 MiB on Windows. The compiler derives its library root from its own
location and permits `ODIN_ROOT` as an override. Installation is extraction plus
an optional PATH entry/symlink; Odin does not currently ship an installer or an
archive-local manual-install README. Stark can keep the good layout while
improving those rough edges.

- [x] Confirm Odin publishes native Linux x64/arm64, macOS x64/arm64, and Windows
  x64 archives. Odin does not currently publish a Windows arm64 compiler archive.
- [x] Confirm Odin copies the curated `base`, `core`, and full `vendor` trees
  beside the compiler rather than resolving them from a package manager.
- [x] Confirm Odin carries the compiler-side LLVM dependency itself: static on
  Linux, bundled dylibs on macOS, and `LLVM-C.dll` on Windows.
- [x] Confirm Odin does not bundle every platform SDK/link prerequisite. Its
  official guide requires Clang on Unix, Xcode Command Line Tools on macOS, and
  MSVC plus the Windows SDK on Windows.
- [x] Confirm Odin's vendor collection contains source ports/bindings and curated
  platform-native binaries. Availability is package/platform specific; it is not
  synthesized from ambient package-manager metadata at import time.
- [x] Confirm Odin's workflow uses native target runners, `workflow_dispatch`,
  Linux/macOS architecture matrices, executable-preserving tarballs on Unix,
  ZIP on Windows, and an archive-local `odin run examples/demo` smoke.
- [x] Select the Odin-style compiler-SDK/host-development boundary for Stark.
  The earlier goal of redistributing Apple and Microsoft SDKs is retired unless
  a later design makes that both legal and materially easier for users.
- [x] Retain Stark-specific improvements over Odin: optional installer,
  generated offline README, strict SDK/package checksums, `stark doctor`, exact
  module ownership, clean external-project smokes, and release signing.

- [x] Confirm GitHub provides standard hosted runners for all six intended
  64-bit rows: Ubuntu x64/arm64, Windows x64/arm64, and macOS Intel/arm64.
  Linux and Windows arm64 runner labels are currently public preview and need a
  fallback policy if GitHub changes availability.
- [x] Confirm .NET supports self-contained Stage0 publishing for `linux-x64`,
  `linux-arm64`, `win-x64`, `win-arm64`, `osx-x64`, and `osx-arm64`.
- [x] Confirm LLVM 22.1.8 publishes upstream binary payloads usable as acquisition
  inputs for the Stage0 private backend on Linux x64,
  Linux arm64, Windows x64, Windows arm64, and macOS arm64. It does not publish
  a macOS x64 binary payload, so Stark must build and attest the required private
  backend files from pinned source or qualify another reproducible source. All
  five official binary inputs are now pinned by URL, size, SHA-256, signature,
  and attestation in `scripts/llvm-22.1.8-assets.json`; this is reviewed input
  identity, not target-native runtime qualification. The same manifest now
  defines the macOS x64 native source-build recipe and its exact CMake/Ninja
  inputs. A native Intel runner has now qualified that recipe, its minimized
  closure, optimized determinism, System archive, and Hello World behavior.
- [x] Confirm Raylib 6.0 publishes Linux x64/arm64, Windows x64/arm64, and a
  macOS package containing both architectures. The current Stark acquisition
  manifest uses only Linux x64, Windows x64, and macOS arm64.
- [x] Confirm the original release automation built only three targets and
  prepared only `Vendor.Raylib`. Repository-controlled automation now records
  all six rows and prepares the complete nine-package Vendor catalog for each
  enabled row; native qualification of the remaining rows stays open.
- [x] Confirm GitHub release assets must each remain below 2 GiB. Odin's much
  smaller releases demonstrate why Stark should package only its compiler-
  private LLVM runtime components rather than whole upstream development archives.
- [x] Confirm standard GitHub-hosted runners expose limited local storage. Peak
  download, extraction, native build, duplicated staging, and recompression
  space must be measured before assuming the full LLVM acquisition payload can
  be processed and reduced to the release allowlist.
- [x] Confirm the current macOS compiler path already follows the Odin-style
  prerequisite boundary by discovering Xcode Command Line Tools/full Xcode
  through `xcrun`/`SDKROOT`.
- [x] Confirm the current Windows compiler path already follows the Odin-style
  prerequisite boundary by expecting Windows SDK/CRT import libraries to be
  visible to the linker driver.
- [x] Confirm .NET self-contained publishing removes the separate .NET-runtime
  install but does not erase Linux native OS-library requirements. The supported
  Linux ABI/distribution baseline must be explicit and tested.

### Product decisions still required

- [ ] Lock the exact host prerequisite versions/components and minimum OS for
  each target. Keep the list no broader than Odin's model and test the smallest
  supported installation.
- [ ] Decide the Linux compatibility contract: supported distributions and
  minimum glibc/OpenSSL/ICU/zlib versions, or a musl/Native-AOT/invariant-
  globalization strategy that materially reduces the host dependency set.
- [ ] Decide the exact Windows prerequisite UX: detect a valid Visual Studio/
  Build Tools plus Windows SDK installation, offer the official installer when
  absent, and document the minimum selected components.
- [ ] Decide whether Windows arm64 is a first-release blocker or a follow-up
  support tier. Odin currently publishes Windows x64 only; Stark may publish
  arm64 only after its compiler, private backend, System package, complete Vendor
  catalog, installer, and native smoke matrix pass on Windows arm64 hardware.
- [ ] Decide the exact macOS prerequisite UX: detect Command Line Tools/Xcode,
  offer `xcode-select --install` when absent, and clearly report completion or
  cancellation.
- [x] Require the complete official Vendor catalog to be represented in release
  metadata. A package may declare a target unsupported, but a package advertised
  in that target's `sdk.json` must be complete; silent omission and host fallback
  are prohibited.
- [ ] Set an archive-size budget informed by Odin and enforce the compiler-private
  backend allowlist. A Stark archive must not approach GitHub's 2 GiB asset limit
  merely because the acquisition input is a full LLVM development tree.
- [ ] Decide code-signing policy: Apple Developer ID signing/notarization and
  stapling for macOS; Authenticode/SmartScreen signing for Windows; signing-key
  custody and GitHub environment approvals for both.
- [ ] Decide minimum supported OS versions for all six rows and encode them in
  target triples, compiler metadata, installers, and test images.
- [ ] Decide whether release candidates and final releases are rebuilt from the
  same commit or whether reviewed candidate bytes are promoted. The latter gives
  stronger exact-byte review; the former requires reproducible-build evidence.

### Required feasibility spikes

- [ ] Produce an extraction-only Stage0 proof for each existing target with .NET
  and ambient LLVM removed from `PATH`; record compiler-side dependencies
  separately from the approved host development layer used during final link.
- [ ] Prototype and qualify extraction of the allowlisted Stage0 private backend
  from LLVM 22.1.8 Linux arm64 and Windows arm64 signed/attested assets.
  - Implemented: both official archives and their signature/attestation assets
    are pinned, target/RID-bound, and cross-checked against the dependency
    selections. Remaining: native extraction, closure trimming, compiler use,
    dependency audit, and external-project smoke evidence on both runners.
- [ ] Reproducibly build the required LLVM 22.1.8 Stage0 private backend files for
  macOS x64 from pinned source, record the minimum deployment target, and compare
  their runtime closure to the other archives.
  - Implemented (2026-07-23): the acquisition manifest selects the exact signed
    and attested LLVM 22.1.8 source archive, CMake 3.31.6, Ninja 1.12.1, a macOS
    11.0 x86-64 deployment target, Release `-O3`, ThinLTO, static LLVM/Clang/LLD,
    PIC, AArch64 and X86 code generators, prefix-map/no-UUID reproducibility
    flags, bounded compile/link parallelism, and a stripped distribution install
    containing only Clang, Clang resource headers, LLD, llvm-ar, and llvm-ranlib.
    The workflow and local release driver pass only the pinned build-tool
    executables into the source build, and backend/release manifests preserve
    source-build and Apple toolchain provenance through packaging and staged-SDK
    validation.
  - Configuration probe (2026-07-23): the hash-matched source and pinned tools
    configured LLVM successfully from an arm64 macOS host for x86-64, retained
    all declared optimization/static/backend facts in `CMakeCache.txt`, and
    generated the original broad install and compiler/linker targets with no
    unused-option warning. This proves recipe/configuration feasibility, not
    native binary qualification.
  - Qualification automation implemented (2026-07-23):
    `.github/workflows/qualify-private-backend.yml` is a non-publishing,
    read-only, manual workflow pinned to `macos-15-intel`. It resolves one
    immutable commit, acquires hash-pinned CMake/Ninja and LLVM source inputs,
    performs the full source build, and invokes the repository-owned
    `qualify-private-backend` C# command. The command independently validates
    the exact manifest schema and recipe, every closure byte/hash, lack of
    untracked development files and symbolic links, hard-link aliases, x86-64
    Mach-O identity, system-only `otool` dependencies, tool execution, and
    repeatable `-O3` ThinLTO object/archive bytes. The same workflow publishes
    native x64 Stage0, builds the release-profile System package with the
    qualified backend, and compiles/runs `examples/hello.stark`. Reports, logs,
    manifests, licenses, and provenance are retained as workflow evidence; no
    SDK archive or GitHub release can be created by this workflow.
  - First native qualification attempt (2026-07-27):
    [run 30310035777](https://github.com/AlexanderBaggett/Stark/actions/runs/30310035777)
    reached the optimized LLVM source build, then failed while linking
    `llvm-min-tblgen`. The source-build path validator had resolved Apple's
    `clang++` symlink to its shared `clang` executable and therefore discarded
    the invocation name that selects C++ driver mode. The repair preserves the
    `clang++` invocation path after validating its resolved target and checks
    `CMAKE_CXX_COMPILER` immediately after configuration so the same failure
    stops before the multi-thousand-step build.
  - Second native qualification attempt (2026-07-28):
    [run 30316120869](https://github.com/AlexanderBaggett/Stark/actions/runs/30316120869)
    completed the 4-hour-40-minute optimized LLVM build, confirming the C++
    driver repair, then failed when the qualifier invoked generic `lld` with
    only `--version`. Unlike `ld64.lld`, generic `lld` requires a driver flavor;
    qualification now probes it with `-flavor darwin --version` and records the
    exact probe arguments in its evidence. This run also exposed GitHub's
    Node.js 20 action deprecation warning; both release workflows now pin
    immutable Node.js 24-native action releases.
  - Third native qualification attempt (2026-07-29):
    [run 30487936413](https://github.com/AlexanderBaggett/Stark/actions/runs/30487936413)
    completed the 5-hour-16-minute optimized LLVM build and passed the backend
    closure, native dependency, tool execution, and optimized determinism gate.
    Its retained evidence is artifact
    `private-backend-macos-x64-30487936413-1` (artifact ID `8745373257`). The
    subsequent Stage0 smoke failed because raw `dotnet publish` names the
    apphost `compiler`, while the workflow tried the public `stark` name that
    is created only by release packaging. The repair validates and reuses the
    raw apphost for both smoke stages. LLVM source-build output is now streamed
    as it is produced instead of being retained in memory until process exit.
  - Fourth and fifth native qualification attempts (2026-07-31 and 2026-08-04):
    [run 30655566724](https://github.com/AlexanderBaggett/Stark/actions/runs/30655566724)
    and
    [run 30869476675](https://github.com/AlexanderBaggett/Stark/actions/runs/30869476675)
    remained active through approximately 4,430 of 4,478 Ninja actions, then
    GitHub canceled each job at its six-hour hosted-runner limit. The underlying
    binaries for all backend tools required by Stark had linked after
    approximately 3 hours 18 minutes; the remaining work was unrelated LLVM
    tooling pulled in by the broad `install/strip` target. The reviewed recipe
    now uses LLVM's
    `LLVM_DISTRIBUTION_COMPONENTS` mechanism and
    `install-distribution-stripped` target to build and install exactly Clang,
    Clang resource headers, LLD, llvm-ar, and llvm-ranlib. Release `-O3`,
    ThinLTO, static linkage, reproducibility settings, and both AArch64 and X86
    code generators remain unchanged, and the exact component set is covered by
    configuration validation and integration tests.
  - Successful native qualification baseline (2026-08-06):
    [run 30944641351](https://github.com/AlexanderBaggett/Stark/actions/runs/30944641351)
    qualified commit `ddf24627821fb02322ddfcd253715cc8343472ff` in approximately
    1 hour 44 minutes on native Intel macOS. Its
    retained evidence is artifact `private-backend-macos-x64-30944641351-1`
    (artifact ID `8909655368`). The qualified closure contains 321 files and
    477,257,369 logical bytes. All seven required tools are x86-64 Mach-O and
    depend only on `/usr/lib/libSystem.B.dylib` and `/usr/lib/libc++.1.dylib`.
    Repeat `-O3` ThinLTO compilation produced identical objects and archives;
    optimized System contained 30 members, and the external Hello World smoke
    printed the expected output. The build used Xcode 16.4 (16F6), macOS SDK
    15.5, and Apple Clang 17.0.0; those identities and executable hashes are now
    locked in the source-build recipe and checked before a costly build starts.
    The dependency selection is therefore `qualified-build`. It remains a
    planned target until the full release/archive/install smoke and oldest-host
    compatibility gate pass.
  - [ ] Re-run qualification against the final immutable release commit and
    retain that successful run URL/artifact identity here.
  - [x] Review `private-backend-report.json` and
    `stage0-smoke-report.json`; confirm the closure size, every `otool`
    dependency, x86-64 identity, repeat-build digests, optimized System archive,
    and Hello World result are acceptable.
  - [ ] Run the output on the oldest supported macOS x64 host, compare its
    closure and archive-size envelope with the other platforms before promoting
    the target from `planned` to `tier-1` and enabling release publication.
- [ ] Measure compressed archive size and peak CI disk use for all six complete
  candidates before choosing runner sizes or artifact transport.
- [ ] Add a native-dependency inventory gate using platform tools (`otool`,
  `readelf`/`ldd`-equivalent inspection, and PE import inspection) and reject
  unexpected absolute paths or non-policy system dependencies.
  - Implemented: `scripts/audit-release-native-dependencies.ps1` inventories
    Mach-O, ELF, and PE payloads under the staged compiler, backend, System, and
    Vendor roots; the release workflow runs it before artifact upload and retains
    the structured report.
  - Remaining: qualify its results against complete native payloads on every
    enabled and planned native runner before closing this spike.
- [ ] Run macOS clean-machine probes before and after Command Line Tools setup;
  verify the installer/doctor experience and compile core Stark, Raylib, GLFW,
  SDL3, and Miniaudio using no Homebrew or repository paths.
- [ ] Run Windows clean-machine probes before and after the minimal official
  Build Tools/Windows SDK setup; verify installer/doctor behavior and every
  Windows-capable Vendor package without ad hoc environment configuration.
- [ ] Run Linux probes in clean x64/arm64 images at the oldest supported baseline
  and enumerate .NET, linker, X11/Wayland, OpenGL/Vulkan, audio, pthread, `dl`,
  and libc dependencies for every official Vendor package.
- [ ] Prototype installer install/repair/upgrade/uninstall/PATH behavior on all
  six native runners before treating installer design as settled.
- [ ] Complete legal review of .NET, LLVM, Raylib, GLFW, SDL3, SQLite, STB,
  Miniaudio, cgltf, Apple SDK-derived material, Microsoft SDK/UCRT material, and
  every transitive native payload actually shipped.

## 1. Release Scope and Policy

- [ ] Choose the first release version and record whether it is an alpha, beta,
  release candidate, or stable release.
- [ ] Choose the release date and name a release coordinator.
- [x] Confirm the intended six-target release matrix and exact target triples;
  enabled versus planned qualification remains explicit in `eng/release/targets.json`:
  - [x] macOS Apple Silicon / arm64 (`osx-arm64`,
    `arm64-apple-macosx11.0.0`)
  - [x] macOS Intel / x64 (`osx-x64`, `x86_64-apple-macosx11.0.0`)
  - [x] Linux x64 (`linux-x64`, `x86_64-unknown-linux-gnu`)
  - [x] Linux arm64 (`linux-arm64`, `aarch64-unknown-linux-gnu`)
  - [x] Windows x64 (`win-x64`, `x86_64-pc-windows-msvc`)
  - [x] Windows arm64 (`win-arm64`, `aarch64-pc-windows-msvc`)
- [x] Restrict public releases to 64-bit architectures. Do not build or publish
  32-bit x86, arm32, or other 32-bit compiler/SDK archives.
- [x] Use `x64` or `x86_64` consistently in asset IDs, manifests, documentation,
  diagnostics, and installer output; never use bare `x86` for a 64-bit target.
- [ ] Give every matrix row a native build/qualification environment. If GitHub
  has no suitable hosted runner, provide a reproducible self-hosted runner or
  a verified cross-build plus native execution/installation qualification.
- [ ] Document the support tier and minimum host requirements for every
  published target.
- [ ] Write the known-limitations list, including incomplete language,
  compiler-backend, host-link, vendor-library, editor, and platform behavior.
- [ ] Define the compatibility promise for the compiler, SDK manifest, package
  image format, source language, and standard library for this release line.
- [x] Confirm that Stage1/self-host work is explicitly out of scope for this
  release and that Stage0 remains the shipped compiler.

## 2. Repository and Public-Project Readiness

- [ ] Audit the complete Git history and current tree for credentials, tokens,
  private keys, private URLs, personal data, generated secrets, and accidental
  large binaries before promoting the release commit.
  - Implemented: redacting current-tree/history scanner, exact fingerprint
    allowlist, large-binary checks, structured output, and deleted-history test
    fixtures in `scripts/audit-public-repository.ps1`.
  - Current-tree result: pass with zero unsuppressed findings and zero scanner
    errors. One user-specific test fixture was neutralized; eight intentional
    loopback/upstream-documentation matches are retained as exact fingerprinted
    exceptions with reasons.
  - History result: 8,871 unique historical blobs scanned with zero scanner
    errors and no credentials, private keys, tokens, SSNs, private-network
    services, or oversized historical binaries. The 116 redacted warnings are
    limited to retired developer-local paths, generated-parser provenance,
    superseded test fixtures, reviewed upstream documentation examples, and
    loopback-only development/test URLs.
  - Remaining: make an explicit release-owner decision on whether those benign
    historical local/provenance paths justify a destructive history rewrite.
    No history mutation or broad history allowlist was performed automatically.
- [ ] Confirm the repository license and copyright notices cover the compiler,
  standard library, examples, generated assets, and redistributed files.
- [x] Add or deliberately defer public project-policy files currently absent:
  - [x] `CONTRIBUTING.md`
  - [x] `CODE_OF_CONDUCT.md`
  - [x] `SECURITY.md`, including the supported-version and private reporting
    policy
  - [x] Document GitHub Releases as the authoritative public changelog instead
    of maintaining a duplicate `CHANGELOG.md` before the first release.
- [x] Add public issue and pull-request templates if contributions will be
  accepted for this release line.
- [ ] Verify repository description, topics, homepage, default branch, branch
  protection, Actions permissions, and release permissions on GitHub.
  - Live GitHub checkpoint (2026-07-21): the repository is public, its default
    branch is `main`, Issues are enabled, GitHub detects the MIT license, and
    the description accurately presents Stark as a performance-focused LLVM
    language. The homepage is blank and topics are empty. No public repository
    rulesets are returned; classic branch protection and Actions/release
    permissions require an authenticated settings review before this task can
    close.
- [ ] Remove or clearly label internal-only, stale, aspirational, and
  contributor-host-specific instructions visible to new users.
- [ ] Confirm ignored/generated artifacts are absent from the release commit and
  a fresh clone builds without untracked local prerequisites.

## 3. Code Freeze and Quality Gates

- [ ] Declare the release-candidate freeze point and allow only approved release
  blockers after it.
- [ ] Build the entire solution in Release configuration from a clean checkout.
  - Current-tree evidence: `dotnet build -c Release Stark.slnx --no-restore`
    with `-warnaserror` passes with zero warnings and zero errors. A clean
    release commit/runner build is still required before closing this gate.
- [ ] Run every Stage0 compiler, standard-library, integration, package-image,
  SDK, vendor, and release-script test suite required by the supported matrix.
  - Current-tree evidence (2026-07-23): all 3,447 tests in `Stark.slnx` pass in
    Release configuration. The release tooling has been moved into the
    repository-owned `Stark.ReleaseTools` C# project under the exact .NET SDK
    pinned by `global.json`; its archive, extraction, configuration,
    candidate-binding, managed restore/license, and publication contracts are
    covered by the solution's C# tests. After the macOS x64 pinned source-build,
    provenance, workflow, and TAR-extraction hardening on 2026-07-23, the
    focused release slice passes 134/134 tests. The final clean release-commit
    run and target-native matrix remain open.
- [ ] Resolve all failures or record an explicit release-blocking decision; do
  not silently convert failures into skips.
- [ ] Audit skips and platform guards so each has a specific and valid reason.
- [ ] Run formatting, static analysis, documentation-link, and repository-hygiene
  checks.
  - Current-tree checkpoint (2026-07-22): the public-repository audit passes
    across all 2,336 tracked and untracked files with zero unsuppressed findings
    or scanner errors, and
    the book-structure gate passes all 40 numbered chapters. The latter now
    avoids Bash 4-only `mapfile`, so it executes correctly under macOS's system
    Bash 3.2. The release-tool C# project remains format-clean; the broader
    integration-test formatting check currently reports whitespace drift in
    pre-existing compiler/SDK/package-image/publication test edits, so full
    formatting/static-analysis/link qualification remains open.
- [x] Keep one repository-owned quality-gate command shared by local release
  builds and GitHub Actions, and prevent matrix builds or publication when it
  fails. [`scripts/run-release-quality-gate.ps1`](../../scripts/run-release-quality-gate.ps1)
  performs exact SDK/configuration validation, public-repository and native
  audits, documentation checks, warning-as-error Release build, the complete
  solution test set, book samples, and installer contracts while retaining
  structured logs and test evidence.
- [ ] Confirm accepted source never relies on unsupported MIR/SSA/LLVM fallback
  paths for the release-supported language surface.
- [ ] Run correctness and performance regression suites against the chosen
  baseline and investigate material compile-time, runtime, or memory regressions.
- [ ] Verify optimization facts survive package images, MIR/SSA lowering, ABI
  lowering, and LLVM emission for the release fixtures.
  - Current-tree ABI checkpoint: package images preserve the source aggregate,
    canonical direct aggregate/return carrier, and the ordered physical LLVM
    parameter-carrier sequence as distinct facts. The macOS arm64 four-byte C
    aggregate case therefore retains canonical `i32` return lowering and its
    rounded `i64` parameter carrier across package-image round trips. Focused
    package-image and AArch64 LLVM tests pass 4/4; the complete optimization-fact
    and target-native release matrix remains open.

## 4. Repository-Controlled GitHub Release Pipeline

The existing [release workflow](../../.github/workflows/release.yml) already has
a manual `workflow_dispatch` entry point, target matrix, archive build, separate
install-smoke stage, and draft GitHub release stage. Keep GitHub Actions as the
orchestrator, but move release truth out of duplicated YAML and into validated
repository-owned data and scripts.

### Single source of truth

Implementation checkpoint (2026-07-16): `eng/release/` now records all six
64-bit rows, the dependency/private-backend inputs, all seven official Vendor
upstream families (represented by nine Stark package images), mandatory archive
contents, and release metadata. Validation emits the enabled matrix used by
both build and install-smoke jobs, or all six rows only under the explicit
nonpublishing `include_planned` diagnostic gate. Three rows remain planned:
Linux arm64 and Windows arm64 have qualified upstream LLVM input identities but
still need full native package qualification; macOS x64 has a natively
qualified pinned source-built backend but still needs the complete SDK archive,
Vendor, installer, oldest-host, and independent install-smoke gates. Planned
status remains separate from backend-input/build qualification so no successful
backend probe can make a target publishable by itself.

#### Implemented managed-dependency pin contract (2026-07-21)

Release builds select an exact SDK/runtime pair, exact NuGet graph, and
RID-specific lock. The security-serviced pair published by Microsoft on
2026-07-14 is **.NET SDK
10.0.302 with .NET runtime 10.0.10**. Microsoft's signed release index identifies
10.0.10 as the active .NET 10 LTS security release and 10.0.302 as its latest
SDK; the channel metadata maps that SDK back to runtime 10.0.10 and publishes
per-RID SDK archive SHA-512 values for all six Stark targets. Evidence:
[release index](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json),
[.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json),
[SDK selection](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json),
and [self-contained runtime selection](https://learn.microsoft.com/en-us/dotnet/core/versions/selection).

The implemented contract is:

- Commit a root `global.json` selecting SDK `10.0.302`, `rollForward: disable`,
  and `allowPrerelease: false`; setup and validation must require the exact
  `dotnet --version` result instead of accepting an installed runner SDK.
- Keep the dependency-manifest value consumed by SDK setup at `10.0.302`, add
  an explicit `runtimeVersion: 10.0.10`, change its pin status to `exact`, and
  retain the official release-metadata URL plus the reviewed per-RID SDK
  archive SHA-512 evidence. Future servicing upgrades are explicit reviewed
  manifest/lock changes, never a wildcard resolution during a release run.
- Restore and publish with `RuntimeFrameworkVersion=10.0.10`. A self-contained
  publish uses this property as the exact runtime framework version; validation
  must also find `Microsoft.NETCore.App.Runtime.<rid>/10.0.10` in the published
  dependency graph before packaging.
- Change Stage0's ANTLR reference to the exact NuGet range `[4.13.1]`. Commit
  one release lock per RID, for example
  `src/packages.linux-x64.lock.json` through
  `src/packages.osx-arm64.lock.json`, generated by an explicit `-r <rid>`
  restore. Select the matching file with `--lock-file-path`, restore with
  `--locked-mode`, then publish with `--no-restore`. A single no-RID lock is not
  sufficient: NuGet rejects it for a RID restore with `NU1004`; a lock containing
  all six RIDs likewise requires the project to declare that same all-RID set.
  Per-RID locks keep each native release runner narrow and independently
  reviewable.
- Use a repository NuGet configuration that clears ambient sources and maps the
  complete Stage0 package closure to `https://api.nuget.org/v3/index.json`.
  Qualify locked restore once with an empty package cache so a developer's
  global cache cannot hide an undeclared input. NuGet documents lock files and
  locked mode as the application-level full-closure contract:
  [PackageReference lock files](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files).

The Stage0 managed package closure is currently exactly one package:
`Antlr4.Runtime.Standard` 4.13.1, with no transitive dependencies for
`.NETStandard2.0`. Its NuGet lock content hash is
`Da5+i4kFHUseJRZGcBG5fmZGpA/Ns180ibrQMxgZzjpQOnENVvSL5gi5HZ8Ncz8/AR2WsKbOg2lMBzjz0HUQcA==`;
the NuGet catalog's signed-package SHA-512 is
`H0gXethck6OjObUJ2qK+9xFUod80Qzfr/gdlEQiC5FAhsRafRglxUiloLiLxF15MrA9AasCOhU4cFWcu4Yuk5w==`.
Evidence: [NuGet package and dependency groups](https://www.nuget.org/packages/Antlr4.Runtime.Standard/4.13.1)
and [immutable catalog leaf](https://api.nuget.org/v3/catalog0/data/2023.09.11.10.16.36/antlr4.runtime.standard.4.13.1.json).

The generated-parser mismatch is resolved. The checked-in lexer, parser, and
visitors were regenerated with ANTLR 4.13.1 and now match the exact 4.13.1
runtime. The release manifest pins the official complete-generator artifact at
SHA-256
`bc13a9c57a8dd7d5196888211e5ede657cb64a3ce968608697e4f668251a8487`.
[`scripts/regenerate-parser.sh`](../../scripts/regenerate-parser.sh) acquires
that exact artifact, verifies it before execution, and invokes the generator
from the repository root so generated comments contain stable `Stark.g4`
provenance rather than a contributor-specific absolute checkout path. An actual
4.13.1 regeneration byte-matched all four checked-in generated files.

The repository now contains `global.json`, the exact compiler project
properties, a source-clearing `NuGet.config`, all six RID lock files, exact SDK
archive and runtime-pack hashes, a managed-restore validator, and matching local
and GitHub workflow commands. Generator and runtime are aligned at 4.13.1. Each
self-contained publish still requires native-runner qualification on its target
OS/architecture.

macOS arm64 current-tree evidence (2026-07-21): a fresh official SDK archive
matched its reviewed SHA-512 and reported exact SDK `10.0.302`/runtime
`10.0.10`; a cold-cache absolute-lock restore passed the managed validator for
ANTLR and both signed runtime packs; the self-contained arm64 compiler published,
executed, and reported `stark-sdk-v1`. The managed report SHA-256 is
`fdf012b09c7cd4eb347a11b3c12d69437e962f478b772573f70fd1cd2797960e`.

The staged-SDK validator verifies the archive contract, exact file checksums,
release inventory, target identity, System and Vendor package-image/archive
hashes, native payload integrity, module ownership, and the complete official
Vendor catalog. Unified, transactional preparation emits all nine package
images on macOS arm64: `Vendor.Raylib`, `Vendor.Raymath`, `Vendor.Rlgl`,
`Vendor.STB.Image`, `Vendor.Miniaudio`, `Vendor.Cgltf`, `Vendor.GLFW`,
`Vendor.SDL3`, and `Vendor.SQLite`.

Frozen macOS-arm64 pipeline proof (2026-07-21): clean snapshot
`b862a66fbd6b7ba2a72284a386e1f44a14cfcb67` produced
`artifacts/current-catalog-release-proof/release-definitive-b862/stark-v31.0.0-current-catalog-proof-macos-arm64.tar.gz`
(168,608,883 bytes, SHA-256
`0a42d965f7f2adad7376e0af6ce4b5b5a756c061ddfe365f71238fc19f98cf1d`).
The exact schema-2 Vendor input was `ready`, contained nine packages and 175
declared files, and assembled with `System` into 10 packages, 50 module owners,
925 checksum-verified files, and the 16 required archive entries. The packaged
private-backend closure contained 341 files and 915,495,626 logical bytes.
Native audit inspected 30 Mach-O files with zero uninspectable files or
violations. Full isolated smoke passed strict doctor, System execution, Raylib
linkage, all eight safe headless Vendor runtime probes, every compiler output
mode, native package/application linkage, and post-relocation repetition. The
installer lifecycle passed installed strict doctor, external System execution,
external `Vendor.SQLite` compile/link/run, receipt-owned uninstall, and cleanup.
Snapshot tests passed the then-current release-script suite 60/60 and focused
.NET release contracts 18/18. A later whole-stage audit found that this frozen archive also
contains the repository's generated `examples/breakout/breakout-raylib` ELF
x86-64 executable and stale
`examples/raylib/dist/libRaylibStark.starkpkg`. It therefore proves the older
pipeline mechanics only; it is content-contaminated and must never be
published. Repository-content staging is now allowlist-driven and the native
audit scans the entire staged SDK, so both classes of contamination fail the
current pipeline.

This is not the publishable candidate. It intentionally predates the later
live-tree expanded-FFI aggregate carrier repair, Linux origin-path audit
hardening, curated repository-content staging, and whole-stage native audit.
Regenerate the next candidate from the eventual clean release commit and repeat
all evidence before sign-off. macOS x64, Linux, and Windows native-runner
qualification also remains required.

- [x] Add a reviewed release-target manifest such as
  `eng/release/targets.json` containing, per target: stable ID, OS, architecture,
  .NET RID, Stark target triple, GitHub runner label, archive kind/suffix,
  executable/library names, compiler-private backend mapping, documented host
  prerequisite, installer kind, and support tier.
- [ ] Add a locked dependency manifest such as
  `eng/release/dependencies.json` containing exact versions, source URLs,
  SHA-256 hashes, licenses, provenance/attestation locations, archive layout,
  and per-target selection for .NET runtime payloads, the allowlisted private
  backend runtime, Raylib, and every other redistributed dependency.
  - Implemented: the manifest and per-target selections are exact and validated,
    including .NET 10.0.10 runtime packs, LLVM 22.1.8, and all official Vendor
    upstream families. Five official LLVM binary inputs are pinned with their
    upstream signature and attestation assets; the validator requires every
    `qualified-input` selection to match that acquisition manifest exactly. The
    macOS x64 selection is an explicit `pinned-source-build` with a validated,
    native-runner-qualified exact recipe and is now `qualified-build`.
    Keep this task open until every planned target's compiler-private backend
    and native payload selection is built and qualified.
- [x] Add an official Vendor package manifest covering the Raylib upstream
  family as separate `Vendor.Raylib`, `Vendor.Raymath`, and `Vendor.Rlgl`
  package images, plus `Vendor.STB.Image`, `Vendor.Miniaudio`, `Vendor.Cgltf`,
  `Vendor.GLFW`, `Vendor.SDL3`, and `Vendor.SQLite`, with pinned source/binary
  identities, build recipes, transitive system link facts, licenses, and target
  support for every release row.
- [x] Add an archive-content manifest/schema describing every mandatory path,
  executable mode, package owner, checksum class, and optional target-specific
  path.
- [x] Add a release metadata file/template for compatibility versions, minimum
  OS versions, known host requirements, and installer defaults.
- [ ] Validate all manifests locally and in CI: unique IDs/triples/RIDs, complete
  six-target coverage, pinned hashes, license presence, safe relative paths,
  and no unresolved placeholders.
  - Implemented for the LLVM acquisition boundary: version/release identity,
    source identity, target/RID coverage, archive URL/SHA-256 agreement,
    signature and attestation identity, positive byte sizes, safe closure paths,
    and exact agreement between acquisition platforms and `qualified-input`
    dependency selections. The central C# validator also covers the build-time-
    only CMake/Ninja manifest: exact versions and immutable release URLs,
    SHA-256/byte pins, archive/executable paths, the macOS universal mapping,
    complete six-target coverage, and rejection of unused assets or input drift.
    It also validates the macOS x64 native host/deployment target, projects,
    code generators, optimization/LTO/static options, build target,
    reproducibility epoch, and bounded parallelism. Broader native-payload and
    clean-runner validation remains open.
- [x] Keep release automation inside the repository's pinned managed toolchain.
  Archive creation/extraction, configuration validation, request planning,
  managed restore/license evidence, candidate comparison/binding, staged SDK
  validation, and GitHub release reconciliation are subcommands of
  `eng/release/Stark.ReleaseTools`; the workflow and PowerShell drivers have no
  secondary scripting-runtime or package-manager dependency. The managed TAR
  extractor handles implicit directories, PAX entries backed by USTAR prefix
  fields, safe links, and empty files; it successfully inventories the real
  pinned CMake macOS archive instead of delegating extraction to an ambient
  scripting package. The 2026-07-23 real-input proof extracted 8,037 files and
  13 symlinks with tree SHA-256
  `24f80beb4e9c8a59c2c168b46dbc732b74a17af52c80d3a9498b1e164da9008d`.
- [x] Generate the GitHub matrix and packaging inputs from these manifests so
  build and install-smoke jobs cannot drift apart.
- [ ] Keep `.github/workflows/release.yml` thin: permissions, triggers, job
  graph, and calls into versioned repository scripts should be its primary
  responsibilities.

### One-click workflow behavior

- [x] Keep a GitHub Actions **Run workflow** button with explicit inputs for
  version, commit/ref, draft, prerelease, publish/no-publish, and optional target
  subset for diagnostics.
- [x] Default manual runs to non-publishing draft prerelease candidates and all
  currently enabled supported targets; require
  an explicit choice to create a public stable release.
- [x] Add an explicit `include_planned` diagnostic input. It is accepted only
  for `publish=false`, expands `all` to the six intended targets, and carries a
  narrowly scoped `AllowPlannedTarget` switch through Vendor preparation and
  packaging. The planner rejects planned targets without opt-in and rejects the
  opt-in before writing a plan whenever publication is requested.
- [x] Add a prepare job that validates the version/ref/manifests, emits the
  dynamic matrix, and refuses a dirty, moving, or mismatched release identity.
  It resolves one immutable commit, rejects expected-SHA mismatches, requires an
  explicit immutable commit for manual publication, rejects partial publication,
  and uploads `release-plan.json` as reviewable workflow evidence.
- [ ] Build compiler, standard library, Vendor packages, native dependencies,
  compiler-private backend, docs, installers, and archive independently for
  every matrix row.
  - Implemented for the macOS x64 private-backend path: source acquisition,
    pinned CMake/Ninja injection, native-host enforcement, deterministic
    Release/ThinLTO build invocation, allowlisted closure trimming, provenance
    emission, packaging cross-checks, staged-release validation, and a dedicated
    native Intel qualification workflow are wired. The qualification workflow
    also publishes native Stage0, builds the optimized System package, and
    compiles/runs Hello World without weakening the release workflow's
    planned-target guard. The native qualification workflow has passed; the
    complete archive/install-smoke route remains open for this planned row.
  - The release workflow now carries each row's backend acquisition kind in the
    generated matrix, caches only checksum-verified source inputs, re-runs the
    full private-backend qualifier for every pinned source build, and retains
    backend manifests/provenance/logs as build evidence. It validates the native
    runner OS/architecture and uses the exact self-contained published Stage0
    apphost—not `dotnet run`—to build optimized System before packaging.
- [ ] Build or acquire every official Vendor native payload before package-image
  generation. Upstream binary absence must select a reproducible pinned-source
  build; it must never select `pkg-config` or omit the package silently.
  - Implemented and macOS-arm64 qualified: the Raylib family, STB Image,
    Miniaudio, cgltf, GLFW, SDL3, and an O3 deterministic SQLite amalgamation
    build whose adapter is precompiled into the bundled archive. The SDL3
    source build uses exact CMake/Ninja inputs, release optimization and ThinLTO,
    and a cross-process work-root lock so concurrent release jobs cannot corrupt
    deterministic intermediates. Remaining: macOS x64 and native Linux/Windows
    qualification of every source/binary recipe.
- [x] Make matrix builds fail independently for useful diagnostics while making
  the aggregate publish job fail closed: never publish a partial target set.
- [x] Upload candidate archives/checksums as workflow artifacts before release
  publication so they can be reviewed and rerun without making a public release.
- [ ] Run independent install/extract smoke jobs on newly provisioned native
  runners after downloading the packaged artifacts; do not smoke only the build
  workspace.
  - Implemented: the independent job downloads the target artifact, reruns the
    archive smoke, installs through the archive-local installer into an isolated
    custom prefix with no PATH mutation, validates installed doctor JSON and an
    external System-using source, uninstalls through the receipt, and always
    retains structured diagnostics.
  - Remaining: execute and qualify this lifecycle on every enabled/planned
    native runner with complete release payloads.
- [ ] Publish only after all target builds, hermetic extraction tests, installer
  tests, content/license validation, checksum generation, and approvals pass.
- [ ] Attach one archive and one checksum per target plus aggregate
  `SHA256SUMS.txt`, release notes, and provenance/attestation artifacts.
  Archive/checksum aggregation is implemented and fail-closed: publication
  requires exact archive/checksum pairs plus successful managed-dependency,
  native-dependency, and stage-validation JSON for every target; raw logs stay
  internal, and `SHA256SUMS.txt` hashes every other public asset exactly once.
  Each report carries an identical archive-derived `candidateBinding` over the
  container bytes, embedded release metadata, source commit, configuration,
  release plan, and deterministic build identity; publication recomputes it.
  Separately review and attach the required provenance/attestation evidence
  before closing this task.
- [x] Make public release publication idempotent and exact at the asset level.
  Before mutation, the reconciler binds the lightweight/annotated tag or draft
  `target_commitish` to the expected full source commit. It may prune stale or
  byte-mismatched assets only from a source-bound draft and journals partial
  deletion progress. Published releases are immutable: exact reruns skip upload,
  while any mismatch fails without mutation. Post-upload verification requires
  exact names, `uploaded` state, byte sizes, and GitHub SHA-256 digests.
  The upload action is forced to retain a draft and receives only the missing or
  replaced subset with overwrites disabled. Repository-owned `configure` is the
  sole visibility transition and runs only after an independent exact remote
  verification. Every workflow artifact is attempt-qualified, and public
  candidates use the isolated `release-candidate-<target>-attempt-<N>` namespace.
- [x] Add workflow concurrency so two releases cannot publish the same version,
  and make retries idempotent without overwriting unrelated releases.
- [x] Minimize GitHub token permissions per job and pin third-party actions to
  reviewed commit SHAs.
  - Implemented: workflow-wide `contents: read` with `contents: write` granted
    only to the gated publish job. Every action is restricted to the reviewed
    immutable 40-hex commit allowlist; tag-only action references are rejected
    by the workflow contract tests.
- [x] Document the GitHub UI procedure: Actions → Release → Run workflow → enter
  version/options → review draft assets → approve/publish.
  - The operator procedure, immutable-commit rules, diagnostic subset behavior,
    draft gate, artifacts to review, and final tag behavior are documented in
    [`eng/release/README.md`](../../eng/release/README.md).

### Local parity and reproducibility

Implementation checkpoint (2026-07-16):
[`scripts/build-release.ps1`](../../scripts/build-release.ps1) now resolves the
same validated target matrix as GitHub Actions through
`Stark.ReleaseTools prepare-release`,
pins an explicit ref to a full commit, derives cache/output roots from SHA-256
identities, and emits a complete command plan without executing it in plan or
dry-run mode. LLVM, Vendor, NuGet package, NuGet HTTP, and managed CLI caches
are all rooted beneath the configuration digest. When the SDL3 source
contributor is active, explicit CMake and Ninja executable paths and hashes are
part of that output identity; ambient tool discovery is rejected. The shared
C# configuration gate validates the pinned build-tool versions, download
identities, archive layouts, executable paths, and all six target mappings
before either local or GitHub orchestration consumes them. Local
execution requires a clean checkout and a target matching the current 64-bit
host. A publishing-equivalent plan fails closed unless it selects every enabled
target and the complete phase pipeline; the local driver never performs the
GitHub publication action itself.

Reproducibility checkpoint (2026-07-16):
`Stark.ReleaseTools compare-candidates`
compares two stage directories, ZIP archives, or TAR archives using exhaustive
ordinal inventories, per-entry SHA-256 hashes and metadata, and exact raw
archive size/SHA-256 evidence. ZIP, TAR, and gzip container metadata remains in
the report rather than being normalized away. Differences are categorized as
inventory, type, content, metadata, candidate format, or archive-container
changes. The command fails by default on every difference, and it never labels
a difference unavoidable without human review.

Deterministic-container checkpoint (2026-07-22):
`Stark.ReleaseTools create-archive`
is the sole release container writer. It uses ordinal member ordering,
normalized timestamps/ownership/modes, one validated top-level directory, safe
relative tar links, flattened regular hard links, and fails closed on Windows
ZIP symlinks or non-portable names. Executable packaging uses the
repository-owned C# release tool under the exact .NET SDK pinned by
`global.json`. The tool's project and compiled assembly hashes, target framework,
and exact SDK/runtime versions are bound into release metadata and build
identity. Prepare, quality, build, and publication all use the same tool; no
separately downloaded scripting runtime participates. Repeated full-stage proof
runs produced byte-identical tar.gz and ZIP outputs. Those proof containers used
the already-recorded frozen stage and therefore remain **non-publishable**
because of its contaminated example payload; determinism does not override
content qualification.

- [x] Provide one local entrypoint, such as
  `pwsh ./scripts/build-release.ps1 -Version ... -Commit <40-hex> -Ref ...
  -Targets ... -Phase All -CMakePath <pinned-cmake> -NinjaPath <pinned-ninja>`,
  that consumes the same manifests and scripts as GitHub Actions.
- [x] Make acquisition, build, package, validation, and smoke phases separately
  invocable for debugging without changing release semantics.
- [x] Cache only checksum-addressed downloads/build inputs; never let an
  unvalidated cache become a release input.
- [x] Record source commit, deterministic build identity, dependency hashes, build options,
  SDK/package format versions, and target facts in every archive.
  - `release.json` schema 2 records a deterministic commit/configuration/plan/
    archive-tool build identity plus the exact package, dependency, Vendor,
    build-option, target, and content-tree facts. Volatile workflow/ref/runner
    facts stay in external evidence. Staged validation recomputes the critical
    identities instead of trusting copied metadata.
- [x] Produce deterministic content ordering/timestamps where feasible and add
  a reproducibility comparison that reports unavoidable differences.
  - The archive writer has byte-identity unit coverage across separate trees,
    and the comparison tool independently reports both logical-entry and raw
    container identities. Official reproducibility evidence must still be
    regenerated from the eventual clean release commit with the required exact
    .NET SDK and repository-owned release-tool assembly.

## 5. Installers, PATH, and Manual Installation

Each archive must be directly usable after extraction and also carry an
optional installer appropriate for its platform. Installers are conveniences,
not a second Stark distribution mechanism. They may invoke the official host
mechanism for an approved platform-development prerequisite, but all Stark,
System, Vendor, and version-sensitive compiler files come from the archive.

Implementation checkpoint (2026-07-16): archive-local Unix and Windows
install/uninstall pairs now provide transactional, receipt-owned, side-by-side
per-user installation, checksum preflight, PATH conflict protection, repair,
force, dry-run, and rollback behavior. macOS lifecycle tests pass. Windows
behavioral qualification and automatic/consented host-prerequisite setup remain
open; PowerShell parsing alone is not target qualification.

### Common installer contract

- [ ] Define per-user, no-administrator default install locations and optional
  system-wide locations for macOS, Linux, and Windows.
- [x] Support explicit `--prefix`/destination, non-interactive, no-PATH-change,
  force/repair, and dry-run modes with consistent semantics across installers.
- [x] Verify `release.json`, `sdk.json`, target/architecture compatibility, and
  every mandatory payload checksum before modifying the machine.
- [x] Copy the complete SDK atomically or transactionally, preserving modes,
  links, relative layout, package ownership, and versioned side-by-side installs.
- [x] Refuse to run an installer on the wrong OS or architecture with an
  actionable message naming the correct asset.
- [x] Never download .NET, Stark packages, Vendor packages, or compiler payloads
  that the release manifest says are bundled. Only acquire an approved host
  prerequisite through its official platform mechanism.
- [ ] Detect missing host prerequisites before copying files, explain their
  source/size/privilege impact, obtain explicit consent, and support a mode that
  prints instructions without installing them.
- [x] Make installation idempotent and define upgrade, downgrade, repair,
  rollback-on-failure, and uninstall behavior.
- [x] Do not overwrite unrelated files or replace a different `stark` command
  without detecting and reporting the conflict.
- [ ] After installation, run the installed `stark doctor --strict` and a small
  compile/link smoke using no files from the extracted source directory.
  - macOS arm64 checkpoint: the independent lifecycle job installs to an
    isolated prefix, runs installed strict doctor, compiles and runs an external
    System executable, compiles/links/runs an external `Vendor.SQLite`
    executable, uninstalls through the receipt, and proves the command is gone.
    The definitive frozen-snapshot report is
    `artifacts/current-catalog-release-proof/installer-lifecycle-definitive-b862-macos-arm64.json`
    (SHA-256
    `c0e57b435adc5e7e62428b9747011177bcc6e61d8fbb10859050a434aaa1765e`).
    Linux and Windows native-runner qualification remains open.
- [x] Generate an installation receipt containing version, source archive hash,
  installed files, chosen prefix, PATH changes, and uninstall information.

### macOS and Linux installer

- [x] Ship `install.sh` in every macOS/Linux archive using portable, auditable
  shell; installing the Stark SDK itself must work offline and without
  PowerShell.
- [ ] On macOS, detect the selected developer directory and usable macOS SDK;
  offer the official `xcode-select --install` flow when Command Line Tools are
  absent and resume verification after the user completes it.
- [ ] On Linux, detect the selected supported distribution/version and only
  offer its documented package-manager command for any approved compiler/
  system-development prerequisites that Stark does not bundle.
- [x] Install per-user by default under a conventional versioned data directory
  and expose `stark` through a user-owned bin directory such as
  `$HOME/.local/bin`.
- [x] Add PATH safely for Bash and Zsh, and support Fish where practical; use a
  clearly delimited idempotent block or a version-independent shim/symlink.
  - The Unix installer owns a delimited, receipt-tracked Bash/Zsh/profile entry;
    manual Fish setup uses `fish_add_path --universal`. Fresh-shell behavior on
    every supported terminal remains a qualification task below.
- [x] Never rewrite an entire shell profile. Back up a modified profile, preserve
  formatting/permissions, and print the exact manual command needed for the
  current shell.
- [ ] Handle login versus interactive shell files deliberately and verify a new
  shell resolves the installed `stark`, not a repository launcher or older SDK.
  - macOS arm64 checkpoint: the isolated Unix installer test starts a fresh
    login-and-interactive Zsh with a clean `PATH`; it resolves the installer-
    owned `$HOME/.local/bin/stark` link and passes `stark doctor --strict`, then
    uninstall removes the link and its delimited profile block. Bash, Fish, and
    target-native Linux qualification remain open.
- [ ] Preserve executable bits and symlinks and account for macOS quarantine/
  Gatekeeper behavior in the documented, security-conscious installation flow.
- [x] Provide `uninstall.sh` or an installer `--uninstall` mode that removes only
  receipt-owned files and its own PATH entry.

### Windows installer

- [x] Ship `install.ps1` in every Windows archive and optionally a small signed
  launcher for users blocked by PowerShell execution policy.
- [ ] Detect Visual Studio/Build Tools through supported discovery, verify the
  exact MSVC and Windows SDK components, and offer Microsoft's official
  installer with the minimal component selection when they are absent.
- [x] Install per-user by default under a versioned `%LOCALAPPDATA%\Stark`
  location; require elevation only for an explicitly requested system install.
- [x] Update the **user** PATH using the supported Windows environment API,
  preserving existing entries, casing, expansion tokens, and length limits.
- [x] Make the current terminal usable immediately when possible and explain
  that already-open terminals may need to be restarted.
- [x] Detect x64 and arm64 hosts accurately, reject the wrong archive, and
  explain that 32-bit hosts are unsupported.
- [x] Provide uninstall behavior that removes only receipt-owned files and the
  exact PATH entry created by Stark.
- [ ] Test PowerShell 7 and the supported built-in Windows PowerShell path if
  both are advertised.

### Installer qualification

- [ ] On every target, test extraction-only use, default per-user install,
  custom-prefix install, no-PATH mode, repeated install, upgrade, repair,
  uninstall, wrong-architecture rejection, checksum failure, and rollback after
  an injected copy failure.
  - Local evidence covers the Unix default/custom/no-PATH/dry-run/repair/
    uninstall/corruption/traversal/wrong-architecture/rollback contracts and the
    isolated archive-to-install-to-uninstall lifecycle. Native CI execution and
    Windows behavioral coverage remain open.
- [ ] Verify PATH changes in fresh Bash, Zsh, Fish, PowerShell, Command Prompt,
  and supported terminal hosts as applicable.
- [ ] Verify the installed SDK remains relocatable as a whole and that installed
  official Vendor packages still resolve without source checkout state.

## 6. SDK and Archive Qualification

- [ ] Produce every archive from one clean release commit through
  [`.github/workflows/release.yml`](../../.github/workflows/release.yml).
- [ ] Confirm compiler informational version, `release.json` version, and
  `sdk.json` version match the intended Git tag exactly.
- [ ] Confirm each archive contains only its advertised target's compiler,
  System package, Vendor packages, native payloads, compiler-private backend,
  licenses, examples, and documentation, while containing **all** required
  files in those categories.
- [ ] Verify every required archive member and SDK payload checksum.
- [ ] Confirm official `System.*` and `Vendor.*` imports resolve through the
  indexed `sdk.json` package owner without repository paths, ambient
  `pkg-config`, or ancestor-directory scanning.
- [ ] Confirm every intended official Vendor package is indexed, checksum-
  complete, target-compatible, and built against the exact release compiler/
  System package identity:
  - [ ] `Vendor.Raylib`
  - [ ] `Vendor.Raymath`
  - [ ] `Vendor.Rlgl`
  - [ ] `Vendor.STB.Image`
  - [ ] `Vendor.Miniaudio`
  - [ ] `Vendor.Cgltf`
  - [ ] `Vendor.GLFW`
  - [ ] `Vendor.SDL3`
  - [ ] `Vendor.SQLite`
  - macOS arm64 checkpoint: all nine entries satisfy this contract in the
    frozen-snapshot 10-package/50-module SDK. These boxes remain open until the same
    target-native evidence exists for every published archive row.
- [ ] Run [`scripts/smoke-release-archive.ps1`](../../scripts/smoke-release-archive.ps1)
  against every downloaded workflow artifact with `-IsolatePath`.
- [ ] From each extracted archive, with only `<sdk-root>/bin` added to `PATH`:
  - [ ] run `stark --help`
  - [ ] run `stark doctor --strict`
  - [ ] compile and run an external hello-world project
  - [ ] build an external System-using project
  - [ ] build and link external projects for every advertised `Vendor.*` package
  - [ ] run safe headless/runtime smokes for every Vendor package whose public
    contract can execute on CI, with graphical/audio hardware paths covered by
    separate target-native qualification
  - [ ] exercise `--check`, MIR, SSA, LLVM, object, library, executable, and
    package-image output paths
  - [ ] move the extracted SDK and repeat representative builds
- [ ] Run the same matrix after using only the optional installer on a clean
  native runner, including acquisition of any missing approved host prerequisite.
  - The downloaded-artifact lifecycle now proves install/doctor/external-check/
    uninstall without source-tree SDK state; the complete output/Vendor matrix
    and consented host-prerequisite acquisition remain to be added and run.
- [ ] Run target-native C ABI fixtures, including the macOS arm64 integer-like
  aggregate parameter/return carrier case.
- [ ] Confirm explicit SDK, LLVM, linker, archiver, target, and native-package
  overrides still work and report what was selected.
- [ ] Review archive sizes and ensure no checkout paths, temporary files,
  caches, debug-only binaries, general-purpose LLVM development files, or
  unnecessary compiler-private backend payloads are included.
- [ ] Test installation instructions in a fresh terminal using the exact public
  archive, not a repository launcher.

## 7. Archive README, Documentation, Examples, and Editor Experience

- [x] Generate a release-specific `README.md` at the root of every archive; do
  not rely on the repository README as the installation manual.
- [ ] Make the archive README usable offline and include:
  - [x] exact supported OS, architecture, target triple, minimum OS, Stark
    version, compiler stage, LLVM version, and archive checksum instructions
  - [x] archive-content overview and what “self-contained” means, including the
    distinction between the shipped compiler-private backend and host linker/SDK
  - [ ] the exact Odin-style host prerequisite, why final native linking needs
    it, how the installer detects it, and the smallest supported installation
  - [x] optional installer invocation, flags, privilege behavior, PATH changes,
    upgrade/repair/uninstall, and troubleshooting
  - [x] complete manual installation steps that copy the entire SDK intact to a
    chosen location and add only its `bin` directory to PATH
  - [x] Bash, Zsh, Fish, PowerShell, and Windows GUI/manual PATH instructions as
    applicable to the target
  - [x] extraction-only usage without installation
  - [x] `stark doctor --strict`, hello-world build/run, and representative
    verification commands for every advertised official Vendor package
  - [x] relocation, side-by-side version, override, and removal instructions
  - [x] known platform limitations and actionable diagnostics
  - [x] license, security-reporting, issue-reporting, documentation, source, and
    release-note links
- [x] Generate target-specific README facts from the release manifests so six
  copies cannot drift by hand.
- [ ] Test every command in every archive README against the packaged candidate
  in CI, including the manual path with the installer deliberately skipped.
  - Implemented: README and INSTALL command blocks are generated through one
    checksummed `release-commands.json` contract. Both extraction-only and
    installed-SDK smokes execute `stark doctor --strict`, `stark --check`,
    `stark build`, and `stark run` against copied archive-owned hello-world
    inputs, and require the run to print `Hello, World!`. Keep this task open
    until those smokes pass on every published target archive.

- [ ] Make the README's first-run path match the published archive, supported
  targets, and current Stark syntax.
- [ ] Verify installation, project, SDK, native-package, FFI, and troubleshooting
  documentation against the release candidate.
- [ ] Build or check every example that is presented as supported.
- [ ] Confirm the hello-world and Raylib examples work when copied into a clean
  external project.
- [ ] Confirm representative STB Image, Miniaudio, cgltf, GLFW, SDL3, and SQLite
  examples build and run safely from clean external projects on every advertised
  target.
- [ ] Ensure diagnostics for missing SDK files, host prerequisites, native
  packages, compiler-private backend files, and wrong target facts are actionable.
- [ ] Verify VS Code syntax highlighting and basic project workflows against the
  release-supported language surface; document installation limitations.
- [ ] Review generated/offline documentation included in the archive for stale
  internal links or unreleased features.

## 8. Legal, Licensing, and Supply Chain

- [ ] Inventory every redistributed third-party component and pin its version,
  source URL, license, checksum, and provenance.
- [ ] Verify LLVM/Clang/LLD license and notice requirements for the exact bundled
  files on every target.
- [ ] Verify Raylib and other bundled Vendor package license files are present
  both in package-owned locations and the release license inventory.
- [ ] Verify source-availability and attribution obligations for redistributed
  native binaries and generated bindings.
- [ ] Generate and retain SHA-256 checksums for every published archive.
- [x] Review GitHub Actions and third-party actions used by the release workflow;
  pin actions to trusted revisions if required by the release policy.
- [ ] Record the compiler-private backend, host-development contract, and package
  provenance needed to reproduce or audit the release later.

## 9. GitHub Release Candidate

- [ ] Merge only the intended release changes and confirm the release branch is
  up to date with the chosen default branch.
- [ ] Run the release workflow manually with a prerelease version, `draft=true`,
  and `prerelease=true`.
- [ ] Confirm all build and independent install-smoke jobs pass for the complete
  target matrix.
- [ ] Download the exact draft-release assets and independently verify filenames,
  contents, checksums, signatures/attestations where used, and clean-machine
  behavior.
- [ ] Prepare release notes covering:
  - [ ] what Stark is and the maturity warning
  - [ ] supported platforms and prerequisites
  - [ ] installation and first commands
  - [ ] major language/compiler/SDK capabilities
  - [ ] known limitations and compatibility expectations
  - [ ] where to report bugs and security issues
- [ ] Obtain explicit release sign-off for compiler correctness, SDK packaging,
  documentation, licensing, and GitHub assets.
- [ ] Keep the candidate as a draft until the asset review and sign-off are
  complete.

## 10. Publication

- [ ] Create the signed or annotated version tag from the approved release
  commit using the exact version embedded in the candidate artifacts.
- [ ] Allow the tag-triggered workflow to rebuild and requalify the final assets,
  or document and verify the controlled promotion mechanism if candidate assets
  are promoted without rebuilding.
- [ ] Verify the final GitHub release contains exactly one archive per supported
  target plus per-archive checksums and `SHA256SUMS.txt`.
- [ ] Verify the release title, tag, prerelease/stable flag, notes, links, and
  installation commands before publishing.
- [ ] Publish the GitHub release.
- [ ] Test the public, unauthenticated download links and installation flow from
  a clean machine or account after publication.

## 11. Immediate Post-Release

- [ ] Monitor release workflow results, downloads, installation reports, and
  high-severity bug reports during the initial release window.
- [ ] Triage reported issues into release blocker, patch release, documentation,
  or future work.
- [ ] Document the patch/retraction procedure and use it if a security,
  correctness, ABI, or archive-integrity defect is discovered.
- [ ] Record final release evidence and lessons in durable documentation only
  where they change an ongoing contract or process.
- [ ] Move unfinished follow-up to GitHub issues or the normal project tracker.
- [ ] Delete this short-lived `ReleasePrep.md` file.

## Final Go/No-Go Gate

The public release is a **go** only when all of the following are true:

- [ ] The supported target matrix is explicit and every target is green.
- [ ] A fresh user can download, extract, diagnose, build, run, and use an
  official Vendor package without repository-specific configuration; this gate
  applies to every package advertised by that target's `sdk.json`.
- [ ] The archive supplies .NET, every Stark/System/Vendor payload, and every
  compiler-private backend component. It contains no complete LLVM development
  distribution or operating-system SDK. The only separately installed
  components are the target's documented Odin-style host-development
  prerequisite and ordinary operating-system facilities.
- [ ] On a machine that already has the documented host prerequisite, extracting
  the archive and adding `bin` to `PATH` is sufficient—no package-specific setup,
  `pkg-config`, repository path, or extra download is needed.
- [ ] On a clean supported machine, the optional installer detects and guides or
  invokes the official prerequisite setup, installs Stark, updates PATH, and
  reaches a green `stark doctor --strict` with minimal user decisions.
- [ ] Every archive supports both extraction-only/manual setup and its optional
  installer, and every supported terminal finds the installed command through a
  tested PATH change.
- [ ] One GitHub **Run workflow** action builds, independently qualifies, and
  prepares the complete target matrix from repository-owned manifests without
  duplicated target truth in workflow YAML.
- [ ] The shipped compiler, package images, native archives, SDK manifest, and
  compiler-private backend are one verified ABI-coherent set.
- [ ] There are no unresolved release-blocking correctness, security, licensing,
  or supply-chain findings.
- [ ] The exact public assets and release notes have received sign-off.
