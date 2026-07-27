# Release Archive Layout

Status: accepted conventional v1 layout for Stage0 release packaging and later
self-hosted releases.

This document defines the archive shape that release assembly, `stark doctor`,
stdlib discovery, vendor discovery, and clean-machine smoke tests should target.
It is a layout contract, not proof that the release workflow already builds the
archive this way. Its contents follow the Odin model: the archive owns the
compiler, the compiler's private LLVM backend dependency, the full curated
System/Vendor collection, examples, and reference material. A small native
development layer remains a host prerequisite and is installed through the
operating system's official mechanism. A complete LLVM development distribution
and an operating-system SDK are deliberately outside the archive.

## Archive Naming

Release assets use one archive per supported host/runtime:

```text
stark-<version>-linux-x64.tar.gz
stark-<version>-linux-arm64.tar.gz
stark-<version>-windows-x64.zip
stark-<version>-windows-arm64.zip
stark-<version>-macos-arm64.tar.gz
stark-<version>-macos-x64.tar.gz
```

Only x64 and arm64 hosts are in scope. The unpacked root directory uses the same
base name without the archive extension.

## Root Layout

The v1 commands live under `bin/`. Users add that directory to `PATH`; the SDK
manifest, bundled libraries, compiler-private backend, and release metadata
remain at the SDK root. The compiler resolves its canonical executable path and
selects the manifest at the parent SDK root, so the layout stays relocatable and
works when the command is reached through a symlink.

`bin/` is only the conventional command/runtime-support directory. Native
vendor archives are not flattened into `bin`; they remain below the package
that owns them under `vendor/dist/<sdk-target>/native/<package>/`.

```text
stark-<version>-<asset-suffix>/
  bin/
    stark[.exe]
    <compiler runtime support files>
  sdk.json
  release.json
  stdlib/
    Stark.toml
    src/
    templates/
    dist/<sdk-target>/
      System.starkpkg
      libSystem.a | System.lib
  vendor/
    README.md
    Stark.toml
    src/
    native/
    dist/<sdk-target>/
      <all target-advertised Vendor package images>
      <all target-advertised Vendor wrapper archives>
      native/<package>/...
    licenses/
  toolchain/
    llvm-22.1.8/
      <allowlisted private backend programs/libraries/resources>
      licenses/
      provenance/
      manifest.json
  licenses/
    Stark/
    LLVM/
    vendor/
  examples/
  docs/
  README.md
  INSTALL.md
  install.sh | install.ps1
  uninstall.sh | uninstall.ps1
  RELEASE.txt
```

For the retained C# Stage0 compiler, `<compiler runtime support files>` includes
the self-contained publish output needed by the executable. A self-hosted
release instead carries the native self-hosted compiler binary and any support
files it actually needs. The release archive normalizes the command name to
`stark` or `stark.exe` regardless of the project file name used to build
Stage0.

The `toolchain/` directory name is retained as an internal compatibility path;
it is not a promise that the archive contains a general-purpose LLVM toolchain.
Stage0's allowlist may contain a private Clang executable and its runtime closure
because Stage0 consumes textual LLVM. A Stage1 archive instead contains the
qualified libLLVM runtime used by its in-process backend. Neither payload is
advertised as a user C/C++ SDK.

Do not require `STARK_HOME` for normal archive use. `STARK_PATH`,
`STARK_TOOLCHAIN_DIR`, `--toolchain-dir`, linker overrides, archiver overrides,
`--llvm-lib`, `STARK_CLANG`, `STARK_LINKER`, `STARK_ARCHIVER`,
`STARK_LLVM_LIB`, and native dependency overrides remain developer/user escape
hatches, not ordinary installation requirements.

`sdk.json` is the runtime identity and resolution contract. The compiler uses
its exact module ownership index and relative package/native paths; it does not
scan `stdlib/`, `vendor/`, the application repository, or parent directories to
guess an installed SDK. `release.json` records release provenance and archive
contents separately.

## Required Runtime Contents

These files are required for ordinary use from a clean machine:

- `bin/stark[.exe]` plus its runtime support files under `bin/`.
- `licenses/managed/`, containing the exact .NET runtime, ASP.NET runtime, and
  ANTLR license/notice evidence for the managed binaries under `bin/`.
- `sdk.json`, with checksums covering every required package and native file.
- `stdlib/dist/<sdk-target>/System.starkpkg` and the platform standard-library native
  artifact, such as `libSystem.a` or `System.lib`.
- The allowlisted `toolchain/llvm-22.1.8/` compiler-private backend payload.
  For Stage0 this is the compatible backend executable(s), builtin resources,
  transitive runtime libraries, licenses, and diagnostic/provenance metadata it
  actually needs to turn textual LLVM into target output. For Stage1 it is the
  qualified libLLVM runtime and its runtime closure. Release assembly must copy
  from an explicit manifest and reject unlisted upstream development files.
- The complete target-advertised official Vendor collection: bindings and
  reference source, package images, native libraries/runtime files, headers,
  examples, licenses, and ordered platform link facts. None may be recovered
  from ambient `pkg-config` or a machine-local package manager.
- Platform SDK/link inputs that are supplied through the host's supported
  development environment remain explicit host requirements: Xcode Command Line
  Tools/full Xcode on macOS and the selected MSVC/Windows SDK components on
  Windows; a supported Clang/native development environment and system ABI/
  development libraries on Linux. The optional installer may invoke the official
  platform mechanism for these prerequisites; `stark doctor` must diagnose them
  separately from a missing or corrupt private backend.

Because each archive is target-specific, its `sdk.json` advertises exactly one
target and the named `<sdk-target>` directories contain only that target's
release-profile artifacts. Do not ship a cross-target package matrix in v1
archives. The resolver follows the manifest rather than inferring target
identity from directory names.

The compiler, package images, Stark wrapper archives, and native payloads must
be built and staged as one ABI-coherent set. A target ABI lowering change
invalidates affected official package artifacts even when the Stark source API
is unchanged. Release assembly must rebuild them; it must not copy a package
image or wrapper archive from an older SDK/compiler build.

## Included Source And Reference Contents

Release archives include source by default:

- `stdlib/src/` and `stdlib/templates/` for debugging, reference, bootstrap
  investigation, and package rebuilds.
- `vendor/src/` plus `vendor/native/` for the official vendor library, including
  Stark bindings and supporting native/shim source.
- `examples/` for the release-qualified language, System, and official Vendor
  examples shipped beside the compiler, following Odin's distribution model.
  Release assembly selects source/configuration/resource extensions from the
  repository-controlled archive-content manifest; it never copies generated
  package images, objects, native libraries, debug data, or built executables
  from a contributor's examples tree.
- `docs/` for an explicitly curated offline reference: user-facing language and
  SDK material, standard-library reference pages, and only the durable internals
  linked by those pages. Short-lived task trackers, release-preparation notes,
  and self-host work ledgers are repository content, not SDK documentation.

The staged-documentation gate resolves every local Markdown link inside the
archive and rejects missing or escaping targets. Release-native dependency
inspection walks the entire staged SDK, not only expected binary directories.
Native binaries, objects, archives, bitcode, and debug payloads are permitted
only below the manifest's approved roots (`bin`, `stdlib`, `toolchain`, and
`vendor`), and an ELF/Mach-O/PE payload for another operating-system family is
always an archive defect.

Ordinary builds should prefer bundled binary package images and native artifacts.
Source fallback is for diagnostics, rebuilds, and development, not the hot path.

## Deterministic Container Contract

`Stark.ReleaseTools create-archive` is the only supported release-container
writer. `package-release.ps1` passes it the completed staging root and the target
archive kind; direct `Compress-Archive` and `tar -czf` calls are not part of the
release path. The writer creates the candidate through a sibling temporary file
and atomically replaces the final regular file only after the container closes.

Both formats contain exactly one top-level directory and enumerate every entry
in ASCII ordinal path order. Source paths must be relative, ASCII, safe on
case-insensitive Windows filesystems, free of reserved device names and ambiguous
trailing characters, and unique under case folding. Devices, sockets, FIFOs,
unsafe reparse points, escaping/dangling links, and an output path inside the
staging tree are rejected before publication.

Container metadata is normalized independently of the build clock and staging
filesystem:

- tar entries use `mtime=0`, `uid=0`, `gid=0`, empty user/group names, zeroed
  device numbers, and deterministic GNU long-name records; the gzip header has
  no filename/comment/extra fields and uses `mtime=0`;
- ZIP entries use the DOS epoch (`1980-01-01 00:00:00`), Unix creator metadata,
  empty comments and no discretionary extra fields (apart from required ZIP64
  records), and .NET's `SmallestSize` Deflate policy under the pinned runtime;
- directories are `0755`, safe relative symbolic links are `0777`, regular
  files with any source executable bit are `0755`, and other files are `0644`;
  setuid, setgid, sticky, and host umask details never enter the archive;
- tar.gz preserves only safe relative symbolic links that resolve inside the
  staged root. Windows ZIP creation rejects every symbolic link because the
  supported Windows extraction and installation path cannot materialize Unix
  ZIP link records reliably;
- regular hard-linked files are flattened to independent regular entries in
  both formats; release semantics do not depend on host filesystem inode
  identity.

Release tooling is the repository-owned
`eng/release/Stark.ReleaseTools/Stark.ReleaseTools.csproj` project. It is built
with the exact SDK pinned by `global.json`; no separately acquired scripting
runtime or package ecosystem participates in release execution. The packager
records the project SHA-256, target framework, exact SDK/runtime versions, and
compiled assembly size/SHA-256 in `release.json`. Those facts participate in
the content-addressed archive build identity. `scripts/resolve-release-tools.ps1`
is the single local resolver and fails if the selected .NET SDK differs from
the repository policy.
Two invocations over an unchanged tree produce byte-identical containers even
when destination filenames or source mtimes differ. The raw archive SHA-256,
not merely an extracted-tree comparison, is the reproducibility result used for
release review.

## Compiler-Private Backend Metadata

`toolchain/llvm-22.1.8/manifest.json` records:

- LLVM version and asset runtime identifier.
- Original official LLVM archive name or URL.
- Upstream archive checksum.
- Source archive name, URL, and checksum.
- The allowlist of files copied into the Stark archive and the runtime reason for
  each file or file class.
- Included LLVM, Clang, LLD, and runtime license files.
- Rejected and intentionally omitted development-toolchain file classes.

`toolchain/llvm-22.1.8/provenance/` stores the downloaded signature and GitHub
attestation sidecars for the binary package and source archive. The backend
manifest supports `stark doctor`, release auditing, and future LLVM upgrades.
It does not expose a public LLVM SDK or make package managers part of Stark
distribution.

## Release Metadata

`release.json` is the machine-readable archive manifest. Schema 2 is generated
deterministically by `scripts/package-release.ps1` and validated again before an
artifact can be uploaded. Its top-level contract is:

- `releaseVersion`, `starkVersion`, and `compilerVersion`, which must describe
  the same release;
- `source`, containing the exact 40- or 64-digit Git commit, its hash algorithm,
  and proof that the tracked and untracked source checkout was clean. Mutable
  trigger refs, runner repository variables, and commit-selection routes remain
  in external workflow/publication evidence so they cannot perturb archive
  bytes;
- `buildIdentity` and the equivalent compatibility alias `workflowIdentity`,
  containing one deterministic SHA-256 identity derived from the immutable
  commit, release/target/configuration/plan facts, release-tool project hash,
  exact .NET SDK/runtime versions, and compiled release-tool assembly hash.
  Workflow run, job, runner, ref, and host facts are excluded
  from archive bytes and retained only in external workflow evidence;
- `buildOptions` and `configuration`, containing the Release/profile/64-bit
  policy and target/backend selections. The build identity carries an
  orchestration release-plan hash when one was supplied, while `configuration`
  carries the shared release-configuration hash, independently verified
  packaging-input hash, and the
  ordinal file/size/SHA-256 inventory from which that packaging hash was made;
- `schemas`, recording the release metadata, SDK manifest, package-image,
  Vendor release-input, compiler-private backend, target, dependency, Vendor
  catalog, archive-content, and metadata-template schema versions;
- `targetFacts`, preserving both the selected release-target row and the SDK
  target/ABI facts, alongside the compatibility target keys;
- `sdk`, `packages`, and `packageSchemaFacts`, preserving the exact `sdk.json`
  hash and every package image's path, format version, image hash, API hash, and
  content hash. Packaging reads each `STARKPKG` binary header and rejects a
  format-version disagreement rather than trusting the manifest alone;
- `dependencies`, recording the dependency-manifest hash and each selected
  dependency's declaration, target selection, source archive when applicable,
  acquisition manifest, and installed content-tree hashes. Its
  `managedLicenseInventory` child identifies the staged manifest, declaration
  hash, package count, and license-file count;
- `vendorCatalog`, recording both the repository catalog identity and the
  staged catalog/release-input identities;
- `paths`, `compilerPrivateBackend`, and `contentIdentities`, recording the
  archive-relative roots, the allowlisted private-backend closure provenance,
  and independently recomputable file-count/byte-count/content-manifest hashes
  for the compiler runtime, standard library, Vendor tree, and backend; and
- `files` plus `contentChecksumManifest`, recording the staged file inventory
  and selecting `release-files.sha256` as the exhaustive final archive checksum
  manifest.

Release packaging fails closed when Git cannot prove the checked-out commit,
the checkout is dirty, the requested/GitHub/HEAD commits disagree, no immutable
build identity is available, the target row disagrees with command-line facts,
the compiler-private backend closure is absent, any package image/schema/hash
disagrees with `sdk.json`, or any dependency/Vendor/configuration input is
missing. Volatile timestamps are deliberately absent from `release.json`.

## Managed Runtime License Evidence

`eng/release/managed-license-evidence.json` is the technical redistribution
inventory for Stage0's self-contained managed closure. It declares all twelve
`Microsoft.NETCore.App.Runtime.<rid>` and
`Microsoft.AspNetCore.App.Runtime.<rid>` 10.0.10 package archives across the six
64-bit RIDs, their exact NuGet SHA-512 identities, and the byte count and
SHA-256 of each `LICENSE` and `THIRD-PARTY-NOTICES` archive entry. The archive
hashes must agree with `dependencies.json`; duplicate facts that drift fail
configuration validation.

`Antlr4.Runtime.Standard` 4.13.1 contains no license payload in its NuGet
archive. Its evidence is therefore the repository-owned copy at
`eng/release/licenses/Antlr4.Runtime.Standard-4.13.1-LICENSE.txt`, pinned to the
official `4.13.1` tag's full source commit, immutable upstream URL, byte count,
and SHA-256. Preparation still verifies the exact signed ANTLR NuGet package so
the evidence cannot silently attach to a different binary.

After the locked restore, `Stark.ReleaseTools prepare-managed-licenses` reads the
absolute NuGet cache selected by `project.assets.json`. The restore uses
NuGet's required-signature mode; the helper then verifies each exact package
archive and NuGet cache hash and requires the matching signature artifacts. It reads only the
declared evidence entries, verifies their bytes, and emits the target-specific
`licenses/managed/` tree and manifest, including each dependency's declared
license expression. Release packaging does not recover
licenses from the published `bin/` tree or from the network. Stage validation
recomputes the declaration identity and every staged file hash, requires the
exact three-package/five-file target closure, rejects extra files, and then the
ordinary exhaustive `release-files.sha256` inventory covers the result.

This closes the machine-verifiable inventory and presence contract. It is not
legal advice or a claim that redistribution has received legal approval;
release sign-off remains a separate human decision.

`sdk.json` is independently required and contains the compiler compatibility
line, package format, structured target/ABI facts, exact module ownership,
package dependency graph, relative image/library/native/runtime/license paths,
ordered native link facts, and required file checksums.

The official Vendor catalog makes native-payload ownership explicit with each
package's `nativePayloadOwner`. A package that names itself must contribute a
nonempty native artifact inventory. A Stark wrapper package may contribute no
native artifacts only when it names a direct package dependency as its owner,
the SDK package dependency graph contains that same edge, and the named package
owns and ships a nonempty native payload. Shared-payload wrappers still ship
their own package image, wrapper archive, license inventory, and checksums. This
is why `Vendor.Raymath` and `Vendor.Rlgl` can reuse `Vendor.Raylib`'s pinned
Raylib payload without duplicating it, while an accidentally empty independent
Vendor package fails release-stage validation.

The compiler's assembly informational version, the Stark release version in
`release.json`, and `sdkVersion` in `sdk.json` must match exactly. The release
workflow stamps the compiler at publish time, and SDK assembly rejects an
unstamped or differently stamped compiler before archive creation. The
`stark-sdk-v1` compatibility line remains a separate runtime contract.

`RELEASE.txt` is the human-readable summary generated from the same facts. It
includes the version, commit, runtime identifier, LLVM version, target triple,
build kind/identity, release-configuration hash, schema versions, and notable
platform requirements.

## GitHub Release Asset Set

Candidate workflow artifacts contain both public evidence and internal logs.
Before publication, `prepare-release-public-assets.ps1` recursively reads the
downloaded artifact layout, validates every archive against its adjacent
SHA-256 file, and requires successful managed-dependency, native-dependency,
and staged-SDK JSON reports for the same target. The caller must supply the
prepare job's exact expected version, source commit, release-configuration
SHA-256, release-plan SHA-256, and comma-separated target set. Unknown files,
duplicate or case-colliding names, orphan checksums/evidence, missing or extra
targets, mixed versions, mismatched archive formats, invalid report identities,
and failed reports stop publication.

Managed dependency validation has an early `--restore-only` fail-fast phase
before compiler publication and a post-package candidate-scoped phase over the
same restore assets. In that second phase it receives `--candidate-sdk-root`.
All three successful validation producers independently include one identical
`validatedCandidate` value covering the staged `release.json` and
`release-files.sha256` hashes and the source/version/target/runtime/build/configuration/plan
identity they actually inspected.

The build job runs `Stark.ReleaseTools candidate-evidence --bind` only after all
three candidate-scoped validations. The binder first requires those native
validation subjects to identify the exact current staged SDK, then compares the
staged and archived bytes of
`release.json` and `release-files.sha256`, reconstructs the content-addressed
release/configuration/plan identity from the archived metadata, hashes the
finished container, and atomically writes one identical `candidateBinding`
into each report. The binding includes the archive name, bytes, and SHA-256;
source commit; release-build, configuration, and plan identities; and hashes
of both SDK metadata files.

The exact public set is one archive and adjacent checksum per target, the three
validated JSON evidence reports per target, and `SHA256SUMS.txt`. The aggregate
manifest hashes every other public asset exactly once in ordinal filename
order. Raw diagnostic logs remain downloadable workflow artifacts but are not
GitHub Release assets.

`prepare-release-public-assets.ps1` independently runs the binder's `inspect`
mode against each downloaded archive and requires all three complete bindings
to equal the archive-derived value. It also requires the three native
`validatedCandidate` subjects to be byte-for-byte equivalent as canonical JSON
and to match the archive-derived metadata hashes and identities. Binding fields
are a closed, portable schema, so extra fields, runner paths, stale hashes,
mismatched reports, and coordinated attempts to replace the embedded
source/configuration/plan facts are rejected before any asset enters the public
set.

Publication is also an exact, source-bound remote-state transaction. All
reconciliation modes receive the full expected source commit. The reconciler
resolves lightweight and annotated Git tag refs before allowing deletion,
upload, metadata configuration, or visibility changes. An existing draft's
`target_commitish` must be the full expected SHA; moving and abbreviated refs
are rejected rather than merely resolved at one instant.

For a draft, `Stark.ReleaseTools reconcile-github-release --mode prune` removes
unexpected assets and same-name assets whose remote bytes are not exact. It
journals the deletion plan and every completed deletion through atomic JSON
replacement, so partial API failure remains auditable. Published releases are never pruned or
overwritten: an exact published rerun emits `upload_required=false` and skips
the upload action, while any published mismatch fails closed. An exact draft is
likewise a no-upload, no-mutation operation.

Prune materializes a separate upload directory containing exactly the missing
assets and replacements for byte-mismatched assets it successfully removes.
Already-exact assets are excluded. Unexpected-only cleanup needs verification
but no release action. First creation materializes every asset. The workflow
uploads only this subset with overwrite disabled and always creates or retains
a draft; planned final visibility is not passed to the upload action.

After creation or mutable-draft upload, `--mode verify` requires exact names,
`uploaded` state, byte size, and GitHub's `sha256:` digest for every local file.
`--mode configure` then independently repeats those checks before its only
possible PATCH. It requires the exact desired draft state, prerelease state,
and release name. Exact draft metadata produces no mutation; stale draft
metadata can be repaired only after the bytes are verified. Clearing draft is
therefore impossible before verification. Exact published reruns remain
read-only, and incompatible requested published metadata fails closed.

The prune, verification, and configuration reports are retained together as
the attempt-unique `publication-evidence-attempt-N` workflow artifact.
Candidate artifacts use the dedicated
`release-candidate-<target>-attempt-<N>` namespace and downloads are bound to
the current attempt. This prevents reruns from consuming prior-attempt
candidates, internal release-plan/build evidence, or their own earlier
publication evidence in the public-asset preparer.

## Install Instructions

The generated `release-commands.json` `steps` array is the canonical quick-start
command model. A shared release-documentation helper renders the marked
platform-specific command block in both `README.md` and `INSTALL.md` from those
steps. Qualification re-renders the block, requires an exact match with the JSON
and both documents, and then executes the same step objects. Updating prose or
the JSON's stored Markdown therefore cannot conceal a command that differs from
the smoke-tested operation.

`README.md` and `INSTALL.md` must be present at the archive root and cover:

- Verify and extract the archive.
- Run the optional platform installer, or follow the complete manual path.
- Add `<sdk-root>/bin` to `PATH`.
- Open a new shell and run `stark doctor`.
- Compile a hello-world or check-only sample.
- macOS requirement for a locally installed Xcode or Command Line Tools SDK.
- Windows requirement for any SDK/CRT pieces the current executable-generation
  path requires.
- Linux requirement for the supported Clang/native development layer and system
  ABI libraries used for final linking. No official Vendor package may require
  a separate `pkg-config` setup.
- Optional environment variables and override flags for advanced use.

The archive—not a package manager—is the Stark distribution. Instructions may
use the operating system's official mechanism only for the declared Odin-style
host-development prerequisite (for example, `xcode-select --install`, Microsoft
Build Tools/Windows SDK, or a supported Linux system-development package). They
must never use a package manager to fill in missing Stark, System, Vendor, or
compiler-version-specific payloads.

## Release Smoke Verification

`scripts/smoke-release-archive.ps1` is the canonical archive smoke entrypoint:

```powershell
./scripts/smoke-release-archive.ps1 `
  -ArchivePath artifacts/release/stark-<version>-<asset-suffix>.tar.gz `
  -TargetTriple <target-triple>
```

The script extracts the archive outside the checkout, clears Stark-specific
environment overrides, puts only the extracted SDK's `bin` directory on
`PATH`, verifies
`stark --help` and `stark doctor`, and compiles the same target through
`--check`, `--emit-mir`, `--emit-ssa`, `--emit-llvm`, `--emit-obj`,
`--emit-lib`, and `--emit-exe`. It builds external projects that import System
and `Vendor.Raylib` without `-I`, project dependencies, `STARK_PATH`, or
`pkg-config`, performs real native links without opening a graphical window,
moves the SDK, and repeats the builds. The suite covers every package advertised
by the target's official Vendor catalog, not only Raylib. Use `-IsolatePath` to
prove the archive does not depend on ambient Stark/.NET/LLVM installations while
retaining only the approved host-development prerequisite. The test separately
proves that the packaged compiler-private backend is present and that no
unlisted LLVM installation is selected.

Each published target must also execute a native C interop fixture that passes
and returns representative by-value aggregates. The macOS arm64 gate includes
a four-byte integer-like aggregate (Raylib `Color` shape) and proves the
AAPCS64 `i64` parameter versus `i32` return distinction. A link-only Raylib
probe is necessary for payload resolution but is not sufficient to catch a
stale or incorrect carrier ABI.

The root-level `stark`/`stark.cmd` launcher generated by a repository build is
a bounded development compatibility form. It selects the sibling development
`sdk.json`, but release assembly must not publish that shape as an alternative
to `bin/stark[.exe]`.

See [Installing the Stark SDK](../Userfacing/InstallingTheStarkSdk.md) for the
user flow and [SDK Layout and Resolution](SdkLayoutAndResolution.md)
for the compiler/development contract.
