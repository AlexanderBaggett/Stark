# Release configuration

This directory is the machine-readable source of truth for Stark release
targets, redistributed dependencies, the official Vendor catalog, required
archive contents, and generated `release.json` facts.

Validate the configuration and print the currently enabled GitHub matrix:

```sh
dotnet run --project eng/release/Stark.ReleaseTools -- validate-config --emit-matrix
```

Validate all six intended 64-bit rows and include planned rows in the emitted
matrix:

```sh
dotnet run --project eng/release/Stark.ReleaseTools -- validate-config --emit-matrix --include-planned
```

`releaseEnabled` is an execution gate, not an omission from the support plan.
The validator always requires all six target identities and complete Vendor
target declarations. A planned row becomes enabled only after its private
backend, System package, entire Vendor catalog, installer, and native smoke are
qualified.

The dependency file deliberately records incomplete qualification as explicit
state rather than fake URLs or checksum placeholders. Validation rejects common
placeholder text and malformed checksums. Release publication must additionally
reject any enabled row whose required input is not qualified.

The current minimum-OS values are an executable CI baseline and are marked
provisional until the release policy decision and clean-machine qualification
lock them.

`Stark.ReleaseTools prepare-release` is the workflow request gate. It resolves the
validated manifests into an immutable plan containing the exact source commit
and selected matrix. A manual run defaults to `publish=false`, `draft=true`,
`prerelease=true`, and all release-enabled targets. Target subsets are allowed
for nonpublishing diagnostics only. Setting `include_planned=true` permits the
three release-disabled rows in that diagnostic matrix; the request is rejected
if `publish=true`. Publishing requires either an expected full commit SHA or a
full SHA as the requested ref, and always requires the complete enabled matrix.

## Run a release candidate from GitHub

1. Push the intended source commit and release-configuration changes to GitHub.
2. Open the repository's **Actions** tab, select the **Release** workflow, and
   choose **Run workflow**.
3. Select the branch containing the workflow definition. Enter a portable
   `version`, such as `v0.1.0-rc.1`.
4. Set `ref` to the branch, tag, or full 40-character commit to build. For a
   review-only candidate, leave `commit` empty, keep `targets=all`, and retain
   the safe defaults: `publish=false`, `include_planned=false`, `draft=true`,
   `prerelease=true`.
5. Choose **Run workflow**. Review the `release-plan` artifact first, especially
   its resolved commit, target IDs, warnings, and configuration identity. Then
   review every `release-candidate-<target>-attempt-<N>` artifact and independent
   `install-smoke-<target>-attempt-<N>` artifact from the same run attempt.

A nonpublishing diagnostic run may select a comma-separated subset of enabled
target IDs. To exercise planned targets, keep `publish=false`, set
`include_planned=true`, and select `all` or a comma-separated subset that may
include `linux-arm64`, `windows-arm64`, or `macos-x64`. Such a run follows the
same archive and independent install-smoke pipeline and produces immutable
candidate artifacts, but it cannot create or update a GitHub Release. Planned
rows are not made publishable by a successful diagnostic run; their manifest
status must be reviewed and promoted separately.

Rows whose matrix declares `private_backend_acquisition=pinned-source-build`
rebuild that backend from checksum-verified source inputs and immediately run
the same closure, architecture, native-dependency, tool, `-O3`, ThinLTO, and
determinism qualifier used by the dedicated backend workflow. The release build
job has a six-hour bound, retains the acquisition/qualification evidence, and
uses the exact self-contained Stage0 apphost produced for that row to build the
optimized System package. Compiled LLVM build directories are never cached;
only immutable downloaded inputs are.

## Mandatory source quality gate

Every release graph is ordered `prepare -> quality -> build -> install-smoke ->
publish`. The matrix build cannot start until the repository-wide `quality` job
passes, and publication independently requires that successful result. The job
checks out the immutable commit selected by `prepare`, acquires the
checksum-verified Linux x64 LLVM backend pinned by
`scripts/llvm-22.1.8-assets.json`, and rejects any backend whose manifest or
Clang version differs from that pin. It exports the verified backend through
the compiler's explicit toolchain environment and prepends its `bin` directory
to `PATH`, so native tests never consume the older ambient Clang supplied by the
GitHub runner. Immutable backend downloads are cached and reused by the later
Linux x64 matrix build. The job then invokes the same repository-owned runner
used by local release builds:

```sh
pwsh -NoProfile -File scripts/run-release-quality-gate.ps1 \
  -OutputDir artifacts/quality
```

The runner fails on the first unsuccessful command and retains a command log for
every completed step, a JSON summary, the Release build binlog, test TRX files,
and the redacted public-tree audit report. The required checks are:

- the exact .NET SDK pinned by `global.json`;
- the repository-owned C# release-tool validation and tests;
- the tracked public-repository tree audit;
- numbered book/documentation structure;
- complete solution restore and warning-as-error Release build;
- the complete solution test suite in Release;
- accepted and rejected book samples through `scripts/check-book-samples.sh`;
- the standalone Unix installer lifecycle harness where applicable, followed by
  release installer contract and host lifecycle tests.

The full solution suite includes the compiler's `ExampleSourceTests`, native
`ExamplesCompileRunTests`, and Vendor binding audits. The explicit
`check-book-samples.sh` step has a different scope: it checks every accepted and
rejected site-book snippet presented as supported. On the Linux GitHub runner,
the standalone `tests/release-installers/test-installers.sh` step exercises the
Unix install/repair/uninstall lifecycle directly instead of relying only on its
.NET test wrapper. A failure still uploads the `quality-gate` artifact, so
diagnostics do not depend on the matrix jobs starting.

For a release-equivalent local check, use the local driver from a clean checkout
whose `HEAD`, `-Commit`, and `-Ref` all identify the same immutable commit:

```sh
commit="$(git rev-parse HEAD)"
pwsh -NoProfile -File scripts/build-release.ps1 \
  -Version v0.1.0-rc.1 \
  -Commit "$commit" \
  -Ref "$commit" \
  -Targets all \
  -Phase Quality
```

The driver resolves `Stark.ReleaseTools` through
`scripts/resolve-release-tools.ps1`, requires the exact .NET SDK pinned by
`global.json`, and records the release-tool project and assembly SHA-256 values.
`-ReleaseToolsPath` may reuse an already-built assembly; otherwise the helper
builds it in Release configuration. There is no separately downloaded scripting
runtime, package installer, or Python dependency in the release trust chain.
`Stark.ReleaseTools.csproj` has no third-party `PackageReference`; its archive,
JSON, hashing, HTTP, and filesystem implementation uses only the .NET base class
libraries supplied by that pinned SDK/runtime.

`Quality` is repository-scoped, so it runs once rather than once per release
target and does not require CMake/Ninja target build tools. `-Phase Plan` and
`-Phase All` include it as the first phase; `-PublishingCandidate` rejects any
plan that omits it. Local diagnostics are written below the selected
checksum-addressed output root at `diagnostics/quality`.

## Deterministic release containers

`scripts/package-release.ps1` delegates both Windows ZIP and Unix tar.gz output
to `Stark.ReleaseTools create-archive`. This repository-owned writer is part of
the release-configuration identity. Its project, target framework, exact .NET
SDK/runtime policy, assembly byte count, and assembly SHA-256 are embedded in
`release.json` and the content-addressed build identity; absolute runner paths
remain external.
It emits one top-level SDK directory in ASCII ordinal order, normalizes archive
timestamps/ownership/modes and gzip/ZIP container fields, preserves safe
relative symbolic links in Unix tar.gz archives, and rejects unsafe,
nonportable, or case-colliding paths. Windows ZIP staging fails closed if it
contains any symbolic link because the supported extraction/install path cannot
reliably materialize Unix ZIP link records. Ordinary `Compress-Archive` and
`tar -czf` output is not release-valid.

Run the focused byte-identity and metadata policy suite with:

```sh
dotnet test tests/compiler.IntegrationTests -c Release --filter FullyQualifiedName~ReleaseToolsTests
```

The suite creates each format twice under different destination names, changes
source mtimes between invocations, and requires raw archive bytes and SHA-256
values to remain identical. Candidate review can additionally use
`Stark.ReleaseTools compare-candidates` to produce categorized container and
entry evidence for independently assembled release trees.

## Executable archive documentation contract

Each target's generated `README.md` and `INSTALL.md` share one marked quick-start
block and a machine-readable `release-commands.json`. The generator derives all
three from the same target facts: the relocatable `bin/stark[.exe]` path, shipped
`examples/hello.stark` source and project, target triple, and the `doctor`,
`--check`, `build`, and `run` operations. `release-commands.json` is a required,
checksummed archive file; it is not optional explanatory metadata.

The JSON `steps` array is the canonical command model. The shared documentation
contract helper renders the platform-specific PowerShell or POSIX Markdown from
those exact steps; generation does not maintain a second command block. During
qualification the helper re-renders the Markdown from `steps` and requires an
exact match before executing those same steps, so changing both documents and
their stored Markdown cannot conceal semantic drift.

Both `scripts/smoke-release-archive.ps1` and
`scripts/smoke-release-install.ps1` load the shared
`scripts/release-documentation-contract.ps1` helper. It rejects unsafe or
unexpected paths, requires the marked blocks in both documents to match the
machine-readable command text exactly, copies the shipped hello inputs to an
isolated work root, and executes every documented operation with the extracted
or installed compiler. The `run` step must print `Hello, World!`. This keeps
first-use documentation executable without leaving build outputs inside the SDK
or a receipt-owned installation.

## Managed license evidence contract

Every target build runs `Stark.ReleaseTools prepare-managed-licenses` immediately after
the exact locked NuGet restore, whose repository-owned NuGet configuration
requires package-signature validation against reviewed NuGet.org repository
certificates and maps every permitted managed/runtime package family. Restore
also disables the SDK's machine-local `library-packs` source and unrelated
transitive framework-pack downloads, so the runner image cannot silently expand
the compiler's dependency closure. Certificate
rotation or a new runtime-pack family therefore requires a reviewed configuration
and validator update. The helper verifies the exact archive
hashes, requires the signature artifacts, and extracts only paths declared in `managed-license-evidence.json` into a
target-owned staging directory. `package-release.ps1` requires that directory
and places it at `licenses/managed/`; `Stark.ReleaseTools validate-stage` rejects a
missing, altered, incomplete, or expanded inventory. The same files are covered
by `release-files.sha256` and described by `release.json` dependency metadata.

The checked-in ANTLR license evidence is necessary because the 4.13.1 NuGet
package has no license file. Its declaration pins the official tag, source
commit, immutable URL, byte count, and hash. These checks establish technical
provenance and payload presence only; they do not replace release-owner legal
review.

## Published asset contract

The publish job downloads all per-target candidate artifacts and runs
`scripts/prepare-release-public-assets.ps1` before invoking GitHub Releases.
The helper recursively handles the directory layout preserved by
`actions/upload-artifact`, rejects unknown files and case-colliding names, and
requires the prepare job's exact version, source commit, release-configuration
SHA-256, release-plan SHA-256, and complete comma-separated target set as
explicit arguments. It fails unless every requested target archive has:

- one adjacent, valid SHA-256 file;
- one successful managed-dependency report for the exact target RID;
- one successful native-dependency report for the exact target; and
- one successful staged-SDK validation report for the exact target.

Managed restore validation is deliberately two phase. The early
`--restore-only` invocation fails before compiler publication if the locked
managed graph is wrong. After packaging, the candidate-scoped invocation reads
the same restore assets with `--candidate-sdk-root` and emits the public
report. Each successful managed, native, and stage producer natively records
an identical `validatedCandidate` subject: the staged root; hashes and sizes of
`release.json` and `release-files.sha256`; and the exact source, version,
target, runtime, content-addressed build, configuration, and plan identities.

After all three candidate-scoped validations succeed,
`Stark.ReleaseTools candidate-evidence --bind` reads `release.json` and
`release-files.sha256` from both the staged SDK and the finished archive,
requires their bytes to agree, rejects a report whose native
`validatedCandidate` differs even when its target name is correct, and only
then atomically adds the same `candidateBinding` to all three reports. That
binding records the actual archive name, byte count, and SHA-256; source
commit; content-addressed release-build identity; configuration and
release-plan hashes; and the two staged metadata hashes.
The binder is deliberately the last successful build step before artifact
upload.

Raw `.log` diagnostics remain workflow artifacts and are not published. The
public release contains the archives, their adjacent checksums, all three JSON
evidence reports per target, and `SHA256SUMS.txt`. That aggregate file lists
every other public asset exactly once in ordinal filename order, so the upload
glob and checksum inventory cannot drift apart.

At publication, the public-asset preparer invokes the binder's read-only
`inspect` mode against the downloaded archive. It recomputes the archive
identity and reconstructs all other binding fields from the metadata inside
that archive, then requires every report's complete binding and native
`validatedCandidate` subject to match exactly. It also compares every archive
to the prepare job's explicit expected identity and rejects missing, extra, or
substituted targets.
The binding contains only portable names, hashes, target facts, and
content-addressed identities. A stale archive, a stale report, one forged
report, or three coordinated forged reports therefore fail before any public
asset is copied.

Publication reconciles GitHub to that validated set instead of merely adding
assets. Every reconciliation mode requires the immutable source commit selected
by `prepare`. Before any draft asset can be deleted, uploaded, configured, or
made public, the reconciler resolves a lightweight or annotated tag chain. An
existing draft must record the full expected commit as its `target_commitish`;
moving and abbreviated refs are rejected even when they currently resolve to
the expected commit.

`Stark.ReleaseTools reconcile-github-release --mode prune` may remove stale or
byte-mismatched assets only from a source-bound draft. Its report is atomically
rewritten before deletion and after every successful deletion, so a failed API call
retains the exact partial-mutation journal. A published release is immutable:
an exact name/state/size/GitHub-SHA-256 match sets `upload_required=false` and
skips the upload action, while any mismatch fails without deleting or
overwriting anything. An exact draft is also a no-upload, no-mutation result.
Prune requires `--upload-directory` and places only missing assets plus local
replacements for successfully deleted byte-mismatched assets there. Existing
exact assets are never put in that directory. Unexpected-only cleanup therefore
sets both `asset_upload_required=false` and `release_action_required=false`.
Release creation sets both values true and materializes the complete desired
set. The release action must use only that directory with
`overwrite_files=false` and must always create or retain a draft, regardless of
the operator's eventual requested visibility.

After the draft upload, `--mode verify` independently requires every remote
asset name to be exact, its state to be `uploaded`, and its size and GitHub
`sha256:` digest to match the local file. Only then may `--mode configure` run
with explicit exact values for `--desired-draft`, `--prerelease`, and
`--release-name`. Configure independently repeats the source and asset checks
before any PATCH. Matching draft metadata is read-only; stale draft name or
prerelease metadata is repaired only after verification. A request to clear
draft is the sole path that makes a release visible. Published metadata is
immutable: an exact rerun performs no PATCH and any requested mismatch fails
closed.

The machine-readable reports are retained in the attempt-unique
`publication-evidence-attempt-N` workflow artifact. Candidate artifact names
use the isolated `release-candidate-<target>-attempt-<N>` namespace, and the
publisher downloads only that exact attempt. A rerun therefore cannot ingest
prior-attempt candidates, the release plan, build evidence, or its own prior
publication evidence.
Reports cover the no-existing-release case, source binding, upload decision,
the exact upload subset, remote byte identities, planned deletions, every
completed deletion, and the verified metadata/visibility transition.

To create a draft prerelease after review, rerun with the exact same version and
source identity, set `ref` to the full commit SHA (or provide that SHA in
`commit`), keep `targets=all`, and set `publish=true`, `draft=true`, and
`prerelease=true`. Publication runs only after prepare, every selected native
build, archive validation, and every independent installer smoke succeed. A
partial target set, moving ref without an expected commit, or failed target
cannot publish.

Only the release coordinator should request cleared `draft` or `prerelease`
state, and only after the repository's release sign-off. Pushing a `v*.*.*` tag
is the final automatic path: it builds the tag's exact commit for all enabled
targets, uploads to a draft, verifies GitHub's stored bytes, then asks the
repository-owned configure mode to publish it (a hyphenated version remains a
prerelease). Do not push the final tag until the candidate bytes, checksums,
licenses, installation results, and release notes have been approved.
