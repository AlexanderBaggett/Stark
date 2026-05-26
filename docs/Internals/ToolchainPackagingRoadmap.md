# Toolchain Packaging Roadmap

Track the work to ship Stark as a self-contained compiler distribution with a bundled Clang/LLD/LLVM toolchain. The goal is that a user can download a Stark release, put it on `PATH`, and compile ordinary Stark programs without separately installing LLVM.

## Goals

- [ ] Ship runtime-specific Stark compiler archives that do not require a separate .NET runtime install.
- [ ] Bundle a pinned Clang/LLD/LLVM toolchain for each supported release platform.
- [ ] Make the compiler prefer its bundled toolchain while still allowing explicit overrides.
- [ ] Bundle or generate the standard library package artifacts needed by ordinary executable and library builds.
- [ ] Provide diagnostics that clearly explain missing platform SDK pieces when a fully bundled path is impossible.
- [ ] Add release verification that proves a clean machine/container can compile and run basic Stark programs from the archive alone.

## Non-Goals For This Roadmap

- [ ] Do not rewrite code generation to use LLVM APIs in-process.
- [ ] Do not statically link libLLVM into the C# compiler executable in the first implementation.
- [ ] Do not hide platform SDK requirements that cannot legally or practically be bundled.
- [ ] Do not remove existing `--linker`, `--archiver`, `--target`, or native dependency override paths.

## Phase 1: Distribution Shape

- [ ] Define the release archive layout.
  - [ ] Choose platform-specific archive names such as `stark-<version>-linux-x64.tar.gz`, `stark-<version>-win-x64.zip`, and `stark-<version>-osx-arm64.tar.gz`.
  - [ ] Define a stable directory layout:

    ```text
    stark-<version>-<rid>/
      bin/
        stark
      stdlib/
        libSystem.a
        System.stark.pkg.json
      toolchain/
        bin/
          clang
          clang++
          ld.lld
          lld-link
          llvm-ar
          llvm-lib
          llvm-ranlib
        lib/
          clang/
      licenses/
    ```

  - [ ] Decide whether `stark` lives at archive root or under `bin/`; provide a root launcher only if it improves ergonomics.
  - [ ] Decide whether standard library source files are bundled for debugging, reference, and package rebuild scenarios.
  - [ ] Document which files are required at runtime versus developer/reference extras.

- [ ] Define supported release IDs for v1.
  - [ ] Linux x64.
  - [ ] Windows x64.
  - [ ] macOS arm64.
  - [ ] macOS x64 if needed.
  - [ ] Linux arm64 if needed.

- [ ] Define minimum host expectations per platform.
  - [ ] Linux libc and startup files expectations.
  - [ ] Windows CRT/SDK expectations.
  - [ ] macOS Xcode Command Line Tools or SDK expectations.
  - [ ] Explicitly document what the bundled LLVM toolchain replaces and what it does not replace.

## Phase 2: Self-Contained Compiler Publish

- [ ] Add release scripts for runtime-specific .NET publish outputs.
  - [ ] `linux-x64`.
  - [ ] `win-x64`.
  - [ ] `osx-arm64`.
  - [ ] Optional `osx-x64`.
  - [ ] Optional `linux-arm64`.

- [ ] Choose publish mode.
  - [ ] Evaluate framework-dependent versus self-contained.
  - [ ] Evaluate single-file versus normal directory publish.
  - [ ] Evaluate trimming only if it does not risk reflection/resource breakage.
  - [ ] Record final choice and rationale in this document.

- [ ] Ensure generated parser/runtime assets are included.
  - [ ] Verify ANTLR runtime dependency is present in published output.
  - [ ] Verify `Stark.g4` linked file does not create a publish-time dependency.
  - [ ] Verify root launcher generation is not relied on by release archives.

- [ ] Add smoke tests for published compiler binaries.
  - [ ] `stark --help`.
  - [ ] `stark sample.stark --check`.
  - [ ] `stark sample.stark --emit-llvm`.
  - [ ] `stark sample.stark --emit-obj`.

## Phase 3: Bundled LLVM Toolchain Acquisition

- [ ] Choose the pinned LLVM version.
  - [ ] Record exact LLVM version.
  - [ ] Record source for each platform artifact.
  - [ ] Record checksums.
  - [ ] Record license files to include.

- [ ] Decide acquisition model.
  - [ ] Use official LLVM release archives where suitable.
  - [ ] Build custom trimmed LLVM toolchains where official archives are too large or inconsistent.
  - [ ] Prefer reproducible scripted acquisition over manual downloads.

- [ ] Define the minimum bundled binary set.
  - [ ] `clang` for LLVM IR and C/native shim compilation.
  - [ ] `clang++` only if needed for native dependencies.
  - [ ] `ld.lld` for ELF linking and ThinLTO.
  - [ ] `lld-link` for Windows linking and ThinLTO.
  - [ ] `llvm-ar` for static library creation on Unix-like platforms.
  - [ ] `llvm-lib` for static library creation on Windows.
  - [ ] `llvm-ranlib` only if needed by archive workflows.

- [ ] Define required resource directories.
  - [ ] Clang builtin headers.
  - [ ] Clang runtime resource directory.
  - [ ] LTO plugin/resources if required by the selected toolchain.
  - [ ] Platform-specific support libraries that may be redistributed.

- [ ] Add size-budget tracking.
  - [ ] Measure uncompressed toolchain size per platform.
  - [ ] Measure compressed archive size per platform.
  - [ ] Identify optional binaries/docs that can be excluded.
  - [ ] Keep license files even when trimming toolchain contents.

## Phase 4: Toolchain Resolution In Compiler

- [ ] Introduce a toolchain resolver abstraction.
  - [ ] Resolve concrete paths for `clang`, linker, archiver, and related tools once.
  - [ ] Avoid hard-coded raw `"clang"`, `"ld.lld"`, `"llvm-ar"`, and `"llvm-lib"` lookups in lowering/linking paths.
  - [ ] Keep command-line overrides as highest priority when explicitly supplied.

- [ ] Define resolution priority.
  - [ ] Explicit CLI overrides: `--linker`, `--archiver`, and any future tool override flags.
  - [ ] `--toolchain-dir <dir>`.
  - [ ] `STARK_TOOLCHAIN_DIR`.
  - [ ] Bundled toolchain beside the compiler executable.
  - [ ] `PATH` fallback.

- [ ] Add new CLI/config surface.
  - [ ] `--toolchain-dir <dir>`.
  - [ ] Optional `STARK_CLANG`, `STARK_LINKER`, and `STARK_ARCHIVER` environment variables if useful.
  - [ ] Optional user config support for project builds.

- [ ] Teach current native tool operations to use resolved paths.
  - [ ] Default target detection.
  - [ ] LLVM IR to object.
  - [ ] Native C source to object.
  - [ ] Executable linking.
  - [ ] Static library creation.
  - [ ] ThinLTO support checks.
  - [ ] macOS SDK discovery should remain platform-aware.

- [ ] Improve failure diagnostics.
  - [ ] Missing bundled tool path.
  - [ ] Non-executable tool file.
  - [ ] Tool exits nonzero.
  - [ ] Tool version mismatch.
  - [ ] Missing platform SDK or system linker inputs.
  - [ ] Native package dependency still requires `pkg-config` or explicit paths.

## Phase 5: Standard Library Bundling

- [ ] Define standard library release contents.
  - [ ] Static library artifact.
  - [ ] Package image sidecar.
  - [ ] Optional source tree for reference/debugging.
  - [ ] Optional generated docs/signature metadata.

- [ ] Make ordinary builds discover bundled stdlib artifacts.
  - [ ] Resolve stdlib next to compiler distribution.
  - [ ] Preserve explicit `-I`, `-L`, and package library override behavior.
  - [ ] Ensure source-tree development builds still work without release layout.

- [ ] Add stdlib package generation to release scripts.
  - [ ] Build with the same bundled toolchain selected for the release.
  - [ ] Ensure target triple and data layout match the platform package.
  - [ ] Emit deterministic package image metadata where practical.

- [ ] Add compatibility tests.
  - [ ] Fresh release archive can compile a hello-world executable using `System.Console`.
  - [ ] Fresh release archive can compile a library package.
  - [ ] Fresh release archive can consume a compiled standard library package without source-tree paths.

## Phase 6: Release Assembly Scripts

- [ ] Add a release assembly script.
  - [ ] Publish the compiler for each runtime ID.
  - [ ] Acquire or locate the pinned LLVM toolchain.
  - [ ] Copy selected toolchain files.
  - [ ] Build standard library package artifacts.
  - [ ] Copy licenses.
  - [ ] Create compressed release archives.
  - [ ] Emit checksums.

- [ ] Add per-platform release manifests.
  - [ ] Compiler version.
  - [ ] Commit SHA.
  - [ ] Runtime ID.
  - [ ] LLVM version.
  - [ ] Tool paths included.
  - [ ] Standard library artifact names.
  - [ ] Archive checksum.

- [ ] Add CI-friendly script modes.
  - [ ] Local developer dry run.
  - [ ] CI release build.
  - [ ] Verify-only mode for an already assembled archive.
  - [ ] Reuse cached LLVM toolchain archives where checksums match.

## Phase 7: Doctor And Diagnostics

- [ ] Add `stark doctor`.
  - [ ] Print compiler version and runtime ID.
  - [ ] Print toolchain resolution source.
  - [ ] Print resolved `clang`, linker, and archiver paths.
  - [ ] Print tool versions.
  - [ ] Print default target triple and data layout.
  - [ ] Print stdlib package path.
  - [ ] Print platform SDK status.

- [ ] Add machine-readable doctor output.
  - [ ] `stark doctor --format json`.
  - [ ] Include enough detail for bug reports and CI diagnostics.

- [ ] Add targeted recommendations.
  - [ ] Missing bundled toolchain.
  - [ ] Missing macOS Command Line Tools.
  - [ ] Missing Linux libc development files.
  - [ ] Missing Windows SDK/CRT pieces.
  - [ ] Native dependency package not found.

## Phase 8: Verification Matrix

- [ ] Clean-machine archive tests.
  - [ ] Linux container with no system LLVM installed.
  - [ ] Windows VM or CI worker with no LLVM installed.
  - [ ] macOS worker with Command Line Tools available but no Homebrew LLVM dependency.

- [ ] Compiler mode tests from archive.
  - [ ] `--check`.
  - [ ] `--emit-mir`.
  - [ ] `--emit-ssa`.
  - [ ] `--emit-llvm`.
  - [ ] `--emit-obj`.
  - [ ] `--emit-lib`.
  - [ ] `--emit-exe`.

- [ ] Toolchain feature tests.
  - [ ] ThinLTO executable build when bundled LLD supports it.
  - [ ] Non-LTO fallback when ThinLTO is unavailable or disabled.
  - [ ] Native C shim compilation.
  - [ ] Package-owned native dependency metadata.
  - [ ] Explicit external linker override.
  - [ ] Explicit external archiver override.
  - [ ] `PATH` fallback with no bundled toolchain.

- [ ] Runtime smoke tests.
  - [ ] Console write.
  - [ ] Math functions.
  - [ ] File IO.
  - [ ] TCP if platform CI permits loopback sockets.
  - [ ] Standard library tests subset that is reasonable for release CI.

## Phase 9: Documentation And Publishing

- [ ] Update user installation docs.
  - [ ] Download archive.
  - [ ] Add `bin` to `PATH`.
  - [ ] Run `stark doctor`.
  - [ ] Compile hello world.
  - [ ] Explain platform SDK expectations.

- [ ] Update contributor docs.
  - [ ] How to build release archives.
  - [ ] How to update bundled LLVM.
  - [ ] How to validate checksums.
  - [ ] How to run clean-machine verification.

- [ ] Update release notes template.
  - [ ] Compiler version.
  - [ ] Bundled LLVM version.
  - [ ] Supported platforms.
  - [ ] Known platform requirements.
  - [ ] Upgrade notes for toolchain resolution changes.

## Open Decisions

- [ ] Which LLVM version should be pinned for the first bundled release?
- [ ] Should official LLVM binaries be used initially, or should Stark build trimmed LLVM toolchains?
- [ ] Should the release include standard library source files by default?
- [ ] Should `--toolchain-dir` apply only to LLVM tools, or also to platform-native helper tools?
- [ ] Should `stark doctor` be a project command, a compiler mode, or both?
- [ ] Should Linux releases target glibc first, musl first, or both?
- [ ] Should Windows releases prefer `lld-link` directly or `clang` as the linker driver?
- [ ] Should macOS releases bundle LLVM but require Command Line Tools for SDK/linking, or attempt a deeper bundled story?

## Acceptance Criteria

- [ ] A user can download a supported-platform Stark archive and run `stark --help` without installing .NET.
- [ ] A user can compile a basic Stark executable without installing LLVM separately.
- [ ] The compiler uses the bundled Clang/LLD/LLVM tools by default from the release archive.
- [ ] Explicit user overrides still work for linker, archiver, target triple, and native dependency paths.
- [ ] `stark doctor` identifies the active toolchain and explains missing platform requirements.
- [ ] CI verifies archive behavior on at least Linux x64, Windows x64, and macOS arm64 before publication.
