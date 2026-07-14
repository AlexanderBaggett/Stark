# Toolchain Packaging Roadmap

Track the work to ship Stark as a self-contained compiler distribution with a bundled Clang/LLD/LLVM toolchain. The goal is that a user can download a Stark release, put it on `PATH`, and compile ordinary Stark programs without separately installing LLVM.

Scope note: this roadmap tracks the minimum useful packaging work for a small
team. Prefer one clear release workflow, human-readable diagnostics, and a small
smoke checklist over a broad release matrix or machine-readable publishing
system. Add heavier release hardening only after the basic release archive is
working and the need is concrete.

Distribution decision: releases are downloadable, relocatable archives produced
by a manually triggered GitHub workflow (or equivalent repository-hosted release
automation) for macOS, Windows, and Linux. Each archive contains the compiled
compiler, any files it needs to operate, the standard library, the vendor
library, required licenses, and install instructions. Package managers are not
part of the roadmap now or later.

Library-source decision: release archives include standard library source by
default, plus the official vendor library source and generated artifacts. The
vendor library is Stark's curated bindings/library tree for common external
software, similar in role to Odin's vendor library; its implementation may live
on another branch until merged, but packaging should treat it as a first-class
release component. Ordinary builds should prefer bundled binary package images
and native artifacts; source is included for reference, debugging, rebuilds, and
bootstrap investigation.

## Goals

- [x] Ship runtime-specific Stark compiler archives that do not require a separate .NET runtime install.
- [x] Bundle a pinned libLLVM plus Clang/LLD/LLVM toolchain for each supported release platform.
- [ ] Make the compiler prefer its bundled toolchain while still allowing explicit overrides.
- [x] Bundle or generate the standard library package artifacts needed by ordinary executable and library builds.
- [x] Bundle the vendor library needed by ordinary Stark development.
- [x] Provide a manual release workflow that creates macOS, Windows, and Linux archives.
- [ ] Provide diagnostics that clearly explain missing platform SDK pieces when a fully bundled path is impossible.
- [x] Add a small release smoke check that proves a clean machine/container can compile and run basic Stark programs from the archive alone.

## Non-Goals For This Roadmap

- [ ] Do not bind LLVM's C++ API; use the LLVM C API as the native boundary.
- [ ] Do not statically link libLLVM into the compiler executable in the first implementation unless a later packaging decision explicitly chooses it.
- [ ] Do not hide platform SDK requirements that cannot legally or practically be bundled.
- [ ] Do not remove existing `--linker`, `--archiver`, `--target`, or native dependency override paths.
- [ ] Do not add package-manager distribution to the roadmap. Homebrew, Scoop,
      apt, npm, and similar package managers are explicitly out of scope.

## Phase 1: Distribution Shape

- [x] Define the release archive layout.
      Decision: use the accepted v1 contract in
      [28-release-archive-layout.md](28-release-archive-layout.md).
  - [x] Use platform-specific archive names such as
        `stark-<version>-linux-x64.tar.gz`,
        `stark-<version>-windows-x64.zip`, and
        `stark-<version>-macos-arm64.tar.gz`.
  - [x] Put `stark[.exe]` and its runtime support files under `bin/`, with
        `sdk.json`, `stdlib/`, `vendor/`, `toolchain/`, `licenses/`,
        `INSTALL.md`, `RELEASE.txt`, and `release.json` at the SDK root.
  - [x] Include standard library source files by default for debugging,
        reference, and package rebuild scenarios.
  - [x] Define vendor library layout and required package/image artifacts.
        The release includes the official vendor library source plus generated
        artifacts needed for normal builds.
  - [x] Document which files are required at runtime versus
        developer/reference extras.
  - [x] Include OS-specific install instructions covering `PATH`, `stark
        doctor`, and optional Stark environment variables.

- [ ] Define supported release IDs for v1.
  - [ ] Linux x64.
  - [ ] Windows x64.
  - [ ] macOS arm64.
  - [ ] macOS x64 if needed.
  - [ ] Linux arm64 if needed.

- [ ] Define minimum host expectations per platform.
  - [x] Linux target/runtime policy.
        Decision: the first Linux release is syscall-backed and no-libc for
        Stark-owned runtime and standard-library code. Ordinary Stark programs
        must not require glibc, musl, libc development packages, or libc wrapper
        calls. If the compiler uses an LLVM target triple such as
        `x86_64-unknown-linux-gnu`, treat that as LLVM target spelling only, not
        a glibc dependency or a promise of separate glibc/musl variants. libc,
        pkg-config, or system library diagnostics apply only when a user-chosen
        native/vendor dependency explicitly requires them.
  - [x] Windows linker-driver policy.
        Decision: use the same Windows executable-generation path for the
        compiler release that Stark uses for ordinary compiled Windows
        programs. In the current host compiler, that means the Clang driver is
        the default executable linker driver unless the user supplies
        `--linker`; `lld-link` remains bundled for ThinLTO, explicit overrides,
        and Clang-driver backend use. Do not create a separate compiler-only
        Windows linking policy.
  - [ ] Windows CRT/SDK expectations.
  - [x] macOS SDK and Command Line Tools policy.
        Decision: macOS release archives bundle Stark's LLVM/toolchain pieces,
        but do not bundle Apple SDKs or Xcode/Command Line Tools content. The
        first macOS release requires a locally installed Apple SDK supplied by
        Xcode or Command Line Tools for SDK headers, platform libraries, and
        linking. Resolution should match current compiler behavior: honor
        `SDKROOT`, check common CLT/Xcode SDK paths, then fall back to
        `xcrun --sdk macosx --show-sdk-path`. `stark doctor` must diagnose a
        missing macOS SDK/CLT installation with a direct remediation message.
  - [ ] Explicitly document what the bundled LLVM toolchain replaces and what it does not replace.

## Phase 2: Self-Contained Compiler Publish

- [x] Add release scripts for runtime-specific .NET publish outputs.
  - [x] `linux-x64`.
  - [x] `win-x64`.
  - [x] `osx-arm64`.
  - [ ] Optional `osx-x64`.
  - [ ] Optional `linux-arm64`.

- [x] Choose publish mode.
  - Decision: ship relocatable per-platform archives containing a compiled
    compiler binary plus any runtime files it needs. Archives must not require a
    separate .NET runtime install. For the retained C# Stage0 compiler this
    means runtime-specific self-contained publish output; self-hosted release
    builds instead contain the native self-hosted compiler binary and its
    required support files.
  - [ ] Evaluate single-file versus normal directory publish only as an
        implementation detail; prefer the simpler reliable shape.
  - [ ] Evaluate trimming only if it does not risk reflection/resource breakage.

- [ ] Ensure generated parser/runtime assets are included.
  - [ ] Verify ANTLR runtime dependency is present in published output.
  - [ ] Verify `Stark.g4` linked file does not create a publish-time dependency.
  - [x] Verify root launcher generation is not relied on by release archives.

- [x] Add smoke tests for published compiler binaries.
  - [x] `stark --help`.
  - [x] `stark sample.stark --check`.
  - [x] `stark sample.stark --emit-llvm`.
  - [x] `stark sample.stark --emit-obj`.

## Phase 3: Bundled libLLVM And LLVM Toolchain Acquisition

- [x] Choose the pinned LLVM version.
  - Decision: the first bundled release targets the LLVM 22.1.x line, pinned to
    LLVM 22.1.8 unless the release branch deliberately updates the pin before
    checksums/artifacts are recorded. Do not track LLVM trunk or LLVM 23
    prereleases for the first bundled release; treat later LLVM updates as
    intentional toolchain-upgrade work.
    Post-self-host follow-up: once the self-hosted compiler is online, migrate
    the bundled toolchain to the latest stable LLVM 23.1.x release so Stark can
    pick up new backend features promptly without destabilizing bootstrap.
  - [x] Record exact LLVM version: 22.1.8.
  - [x] Record required LLVM C API symbols for the supported backend slice:
        `LLVMGetVersion`, `LLVMDisposeMessage`, `LLVMContextCreate`,
        `LLVMContextDispose`, `LLVMModuleCreateWithNameInContext`,
        `LLVMDisposeModule`, `LLVMCreateBuilderInContext`,
        `LLVMDisposeBuilder`, `LLVMVerifyModule`, `LLVMPrintModuleToString`,
        `LLVMInitializeNativeTarget`, `LLVMInitializeNativeAsmPrinter`,
        `LLVMInitializeNativeAsmParser`, `LLVMGetDefaultTargetTriple`,
        `LLVMGetTargetFromTriple`, `LLVMCreateTargetMachine`,
        `LLVMDisposeTargetMachine`, `LLVMTargetMachineEmitToMemoryBuffer`,
        `LLVMDisposeMemoryBuffer`, `LLVMGetBufferStart`, `LLVMGetBufferSize`,
        `LLVMSetTarget`, `LLVMSetDataLayout`, `LLVMInt1TypeInContext`,
        `LLVMInt8TypeInContext`, `LLVMInt32TypeInContext`,
        `LLVMInt64TypeInContext`, `LLVMVoidTypeInContext`, `LLVMPointerType`,
        `LLVMFunctionType`, `LLVMAddFunction`,
        `LLVMAppendBasicBlockInContext`, `LLVMPositionBuilderAtEnd`,
        `LLVMConstInt`, `LLVMBuildRet`, `LLVMBuildRetVoid`, `LLVMAddGlobal`,
        `LLVMAddGlobalInAddressSpace`, `LLVMSetInitializer`,
        `LLVMSetGlobalConstant`, `LLVMSetLinkage`, `LLVMSetVisibility`,
        `LLVMSetDLLStorageClass`, `LLVMSetUnnamedAddress`, `LLVMSetAlignment`,
        `LLVMSetSection`, `LLVMBuildLoad2`, `LLVMBuildStore`, `LLVMBuildGEP2`,
        `LLVMBuildInBoundsGEP2`, `LLVMBuildCall2`, `LLVMSetVolatile`,
        `LLVMSetOrdering`, `LLVMSetInstructionCallConv`, `LLVMSetTailCall`,
        `LLVMGetEnumAttributeKindForName`, `LLVMCreateEnumAttribute`,
        `LLVMAddAttributeAtIndex`, `LLVMAddCallSiteAttribute`,
        `LLVMGetMDKindIDInContext`, `LLVMMDStringInContext2`,
        `LLVMMDNodeInContext2`, `LLVMMetadataAsValue`, `LLVMValueAsMetadata`,
        `LLVMSetMetadata`, `LLVMCountParams`, `LLVMGetParam`,
        `LLVMSetFunctionCallConv`, `LLVMBuildBr`, `LLVMBuildCondBr`,
        `LLVMBuildUnreachable`, `LLVMBuildPhi`, `LLVMAddIncoming`,
        `LLVMBuildAdd`, `LLVMBuildNSWAdd`, `LLVMBuildNUWAdd`,
        `LLVMBuildSub`, `LLVMBuildNSWSub`, `LLVMBuildNUWSub`,
        `LLVMBuildMul`, `LLVMBuildNSWMul`, `LLVMBuildNUWMul`,
        `LLVMBuildUDiv`, `LLVMBuildExactUDiv`, `LLVMBuildSDiv`,
        `LLVMBuildExactSDiv`, `LLVMBuildURem`, `LLVMBuildSRem`,
        `LLVMBuildAnd`, `LLVMBuildOr`, `LLVMBuildXor`, `LLVMBuildShl`,
        `LLVMBuildLShr`, `LLVMBuildAShr`, `LLVMBuildICmp`, and
        `LLVMBuildSelect`.
  - [x] Record source for each platform artifact.
  - [x] Record checksums.
  - [x] Record license files to include.

- [x] Decide acquisition model.
  - Decision: use official LLVM release archives for the first bundled release.
    Copy only the required files into Stark's release archive, and record the
    source archive/asset plus checksum for each platform. Do not make Stark
    maintain custom LLVM build infrastructure during self-hosting. Build custom
    trimmed LLVM toolchains later only if official packages are missing required
    pieces, are inconsistent across target platforms, or create a concrete
    archive-size problem.
  - [x] Use official LLVM release archives where suitable.
  - [~] Build custom trimmed LLVM toolchains where official archives are too large or inconsistent.
        The official LLVM 22.1.8 macOS arm64 archive has the required Clang,
        LLD, archive tools, and Clang resources, but it exposes LLVM only as
        static component archives. Stage0 release acquisition now trims that
        verified asset to the tools its textual-LLVM backend actually uses.
        A reproducible macOS build that supplies a loadable `libLLVM` remains
        required before the direct Stage1 backend can be released.
  - [x] Prefer reproducible scripted acquisition over manual downloads.

- [~] Define the minimum bundled binary set.
  - [~] `libLLVM` shared library for primary in-process object emission.
        It is required by the Linux/Windows asset contracts but has not yet
        passed final cross-platform qualification, and it is not present in
        the official macOS arm64 asset; static component archives are not
        mislabeled as a loadable backend library.
  - [x] `clang` for native C/shim compilation.
  - [x] `clang++` only if needed for native dependencies.
  - [x] `ld.lld` for ELF linking and ThinLTO.
  - [x] `lld-link` for Windows ThinLTO, explicit linker overrides, and any
        Clang-driver link path that needs the bundled Windows LLD backend.
  - [x] `llvm-ar` for static library creation on Unix-like platforms.
  - [x] `llvm-lib` for static library creation on Windows.
  - [x] `llvm-ranlib` only if needed by archive workflows.

- [ ] Define required resource directories.
  - [ ] libLLVM shared-library location and runtime search-path strategy.
  - [x] Clang builtin headers.
  - [x] Clang runtime resource directory.
  - [ ] LTO plugin/resources if required by the selected toolchain.
  - [ ] Platform-specific support libraries that may be redistributed.

- [ ] Add size-budget tracking.
  - [ ] Measure uncompressed toolchain size per platform.
  - [ ] Measure compressed archive size per platform.
  - [ ] Identify optional binaries/docs that can be excluded.
  - [ ] Keep license files even when trimming toolchain contents.

## Phase 4: Toolchain Resolution In Compiler

- [x] Introduce a toolchain resolver abstraction.
  - [x] Resolve concrete paths for libLLVM, `clang`, linker, archiver, and related tools once.
  - [x] Avoid hard-coded raw `"clang"`, `"ld.lld"`, `"llvm-ar"`, `"llvm-lib"`, and libLLVM lookups in lowering/linking paths.
  - [x] Keep command-line overrides as highest priority when explicitly supplied.

- [x] Define resolution priority.
  - [x] Explicit CLI overrides: `--linker`, `--archiver`, and any future tool override flags.
  - [x] Explicit libLLVM override when provided.
  - [x] `--toolchain-dir <dir>`.
        Decision: `--toolchain-dir` names a Stark toolchain root, not only an
        LLVM directory. It applies to bundled/override LLVM tools, libLLVM, and
        Stark-shipped helper tools resolved from that root. It does not replace
        external platform SDKs, CRTs, or Command Line Tools; those remain
        platform requirements diagnosed separately by `stark doctor`.
  - [x] `STARK_TOOLCHAIN_DIR`.
  - [x] Bundled toolchain under the active SDK root selected from
        `bin/stark[.exe]`.
  - [x] `PATH` fallback.

- [x] Add new CLI/config surface.
  - [x] `--toolchain-dir <dir>`.
  - [x] Optional `--llvm-lib <path>` if useful for developer/debug builds.
  - [x] Optional `STARK_CLANG`, `STARK_LINKER`, and `STARK_ARCHIVER` environment variables if useful.
  - [x] Optional `STARK_LLVM_LIB` environment variable if useful.
  - [x] Optional user config support for project builds.

- [ ] Teach current native tool operations to use resolved paths.
  - [x] Default target detection.
  - [ ] libLLVM load/version validation.
  - [ ] LLVM module verification and object emission through libLLVM.
  - [ ] Optional textual LLVM inspection artifact emitted from the in-memory
        module.
  - [x] Native C source to object.
  - [x] Executable linking.
  - [x] Static library creation.
  - [x] ThinLTO support checks.
  - [ ] macOS SDK discovery should remain platform-aware.

- [ ] Improve failure diagnostics.
  - [ ] Missing libLLVM path.
  - [ ] libLLVM version mismatch or missing required C API symbol.
  - [ ] Missing bundled tool path.
  - [ ] Non-executable tool file.
  - [ ] Tool exits nonzero.
  - [ ] Tool version mismatch.
  - [ ] Missing platform SDK or system linker inputs.
  - [ ] Native package dependency still requires `pkg-config` or explicit paths.

## Phase 5: Standard And Vendor Library Bundling

- [~] Define standard library release contents.
  - [x] Static library artifact.
  - [x] Binary package image artifact for compiler loading.
  - [ ] Inspector support to generate deterministic JSON/text package-image views
        on demand.
  - [x] Source tree for reference/debugging/rebuilds.
  - [ ] Optional generated docs/signature metadata.

- [x] Define vendor library release contents.
  - [x] Official vendor library source tree.
  - [x] Binary package image artifacts for compiler loading where applicable.
  - [x] Native/static library artifacts or metadata required by bundled bindings.
  - [x] License files for bundled bindings and redistributed native pieces.
  - [x] Deterministic generated metadata, if the vendor library needs compiler
        inspection without reparsing source.

- [x] Make ordinary builds discover bundled stdlib and official vendor artifacts.
  - [x] Use the compiler-core `sdk.json` index as the single installed-library
        resolver for direct-file and project commands.
  - [x] Implement explicit `--sdk-root`, `STARK_SDK_ROOT`, then bounded
        canonical compiler-executable-relative precedence: the parent SDK root
        for `bin/stark[.exe]`, with sibling-manifest compatibility limited to
        the generated repository development launcher.
  - [x] Retain stage/build-local bootstrap inputs under
        `build/<profile>/<target-triple>/<stage>/` while stage SDK manifests are
        completed.
  - [x] Implement source-tree development only through manifest-declared
        `developmentSourceRoots`; do not walk application ancestors.
  - [x] Implement installed lookup only through the manifest at the parent of
        `bin/`; do not probe arbitrary compiler parents or unmanifested
        `stdlib`/`vendor` directories.
  - [x] Validate package image, native library, target/data-layout/profile,
        compiler compatibility, package format, and required checksums.
  - [x] Keep ordinary discovery free of hidden global package search and network access.
  - [x] Include SDK manifest identity in project incremental stamps and
        dependency LLVM cache keys.
  - [x] When discovery fails, report the selected SDK/stage context and active
        target/profile/stage.

- [x] Add stdlib package generation to release scripts.
  - [x] Build with the same selected release toolchain.
  - [x] Ensure target triple and data layout match the SDK descriptor.
  - [x] Emit deterministic binary package images and `sdk.json`.

- [ ] Add compatibility tests.
  - [ ] Explicit stdlib override wins over all discovered candidates.
  - [ ] Stage/build-local stdlib artifacts are selected during bootstrap builds.
  - [x] Source-tree development builds work through the generated repository
        development SDK manifest.
  - [x] Installed release builds find bundled stdlib artifacts from the SDK
        manifest without source-tree paths.
  - [x] Discovery rejects target/profile/package-metadata mismatches with clear diagnostics.
  - [x] Discovery failure reports SDK/stage context and active target/profile/stage.
  - [x] Discovery does not silently use global or network package locations.
  - [~] Fresh release archive can compile a hello-world executable using
        `System.Console` (macOS arm64 proven; remaining platform gates open).
  - [~] Fresh release archive can compile a library package (remaining platform
        gates open).
  - [~] Fresh release archive can consume a compiled standard library package
        without source-tree paths (macOS arm64 proven; remaining platforms open).

## Phase 6: Minimal Release Assembly

- [~] Add one release assembly script/workflow.
  - [x] Add a manually triggered GitHub Actions workflow (or equivalent) that
        creates release archives for macOS, Windows, and Linux.
  - [x] Publish the compiler for each runtime ID.
  - [x] Acquire or locate the pinned LLVM toolchain.
  - [x] Copy selected toolchain files.
  - [x] Build standard library package artifacts.
  - [x] Build or copy vendor library artifacts.
  - [x] Copy licenses.
  - [x] Copy `INSTALL.md` with per-OS setup instructions.
  - [x] Create compressed release archives.
  - [x] Emit a simple `RELEASE.txt` with compiler version, commit SHA, runtime
        ID, LLVM version, and included stdlib/vendor/toolchain paths.
  - [x] Emit archive checksums if the archive is published outside the repo.

- [ ] Keep script modes minimal.
  - [ ] Local developer dry run.
  - [ ] Build archive.
  - [x] Smoke-test archive.

## Phase 7: Doctor And Diagnostics

- [ ] Add `stark doctor`.
  - Decision: `stark doctor` is a top-level compiler command, not a project-only
    subcommand.
  - [ ] Print compiler version and runtime ID.
  - [ ] Print toolchain resolution source.
  - [ ] Print resolved `clang`, linker, and archiver paths.
  - [ ] Print tool versions.
  - [ ] Print default target triple and data layout.
  - [ ] Print stdlib package path.
  - [ ] Print platform SDK status.

Machine-readable doctor output is not part of v1 self-host packaging. Add a
structured output mode later only if bug reports or automation make the need
concrete.

- [ ] Add targeted recommendations.
  - [ ] Missing bundled toolchain.
  - [ ] Missing macOS Command Line Tools.
  - [ ] Missing Linux native dependency inputs when a selected vendor/native
        library requires libc, pkg-config, or system libraries.
  - [ ] Missing Windows SDK/CRT pieces.
  - [ ] Native dependency package not found.

## Phase 8: Minimum Release Smoke Tests

- [~] Clean-machine archive smoke tests.
  - [ ] Linux container with no system LLVM installed.
  - [x] Windows and macOS archive checks before release when those archives are
        being published.

- [x] Compiler mode tests from archive.
  - [x] `--check`.
  - [x] `--emit-mir`.
  - [x] `--emit-ssa`.
  - [x] `--emit-llvm`.
  - [x] `--emit-obj`.
  - [x] `--emit-lib`.
  - [x] `--emit-exe`.

- [~] Toolchain feature tests.
  - [ ] ThinLTO executable build when bundled LLD supports it.
  - [ ] Non-LTO fallback when ThinLTO is unavailable or disabled.
  - [x] Native C shim compilation.
  - [x] Package-owned native dependency metadata.
  - [ ] Explicit external linker override.
  - [ ] Explicit external archiver override.
  - [ ] `PATH` fallback with no bundled toolchain.

- [x] Runtime smoke tests.
  - [x] Console write.
  - [x] Math functions.
  - [x] File IO.
  - [x] Small standard library subset that is reasonable to run locally before
        release.

## Phase 9: Documentation And Publishing

- [x] Update user installation docs.
  - [x] Download and keep the complete archive together.
  - [x] Add the SDK's `bin` directory to `PATH`.
  - [x] Document SDK overrides as advanced/development-only inputs.
  - [x] Run `stark doctor`.
  - [x] Compile hello world.
  - [x] Explain that non-redistributable platform prerequisites are diagnosed
        separately by `doctor`.

- [~] Update contributor docs.
  - [x] Document SDK layout, root precedence, development manifests, reserved
        namespaces, and cache identity.
  - [ ] How to build release archives.
  - [ ] How to run the manual release workflow.
  - [ ] How to update bundled LLVM.
  - [ ] How to run the release smoke check.

- [ ] Update release notes template.
  - [ ] Compiler version.
  - [ ] Bundled LLVM version.
  - [ ] Supported platforms.
  - [ ] Known platform requirements.
  - [ ] Upgrade notes for toolchain resolution changes.

## Open Decisions


## Acceptance Criteria

- [ ] A user can download a supported-platform Stark archive and run `stark --help` without installing .NET.
- [ ] A user can compile a basic Stark executable without installing LLVM separately.
- [ ] The compiler uses the bundled Clang/LLD/LLVM tools by default from the release archive.
- [ ] Explicit user overrides still work for linker, archiver, target triple, and native dependency paths.
- [ ] `stark doctor` identifies the active toolchain and explains missing platform requirements.
- [ ] The release workflow verifies archive behavior for each platform being
      published, automated when practical.
