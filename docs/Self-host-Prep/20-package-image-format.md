# Package Image Format

Status: WIP, decision locked.

This document records the self-hosting package-image format decision. It is a
logical artifact policy and implementation checklist, not yet a byte-level
binary encoding spec.

## 1. Decision

Use a binary package image as the compiler's normal load format, and keep
deterministic JSON/text forms for inspection, debugging, tests, and export.

Package builds emit the binary load artifact. Human-readable forms are produced
on demand through `stark inspect-pkg`. A build may later grow an explicit
debug/test flag for JSON or text sidecars, but sidecars are not part of the
normal package-build contract.

The binary artifact is the blessed compiler-load path. JSON/text artifacts are
human-facing inspection surfaces and should not be required on the hot path for
ordinary dependency loading.

This resolves OQ-09 as a binary-first package image with lightweight inspection
views. It deliberately avoids a broad artifact-combination matrix for v1.

## 2. Artifact Roles

| Artifact | Role | Loadable by compiler | Human diff/inspect |
|---|---|---|---|
| binary package image | canonical dependency load format | yes | through inspector |
| JSON package image | deterministic inspection/export format | no for the normal path | yes |
| text package dump | deterministic readable summary | no | yes |

The host compiler now emits and loads a binary `.starkpkg` container by default
(STARKPKG magic, format version, Brotli-compressed canonical JSON payload), with
`--package-image-json` as the opt-in JSON sidecar and `--inspect-pkg` rendering
JSON/text from either form. Legacy `.starkpkg.json` artifacts still load during
migration. The self-hosted compiler still owns the long-term sectioned byte-level
encoding described below; the host container exists so the default artifact and
tooling contracts are binary-first before the port.

Conventional names can be decided with the artifact-layout work, but the
intended split is:

- `.starkpkg` for the binary load artifact
- `stark inspect-pkg --format json` for deterministic JSON inspection/export
- `stark inspect-pkg --format text` for deterministic text inspection/export

## 3. Logical Model

The package image concept does not change. It remains a compiler-owned package
boundary artifact carrying structured sections such as:

- package/root identity
- module list
- source-surface facts
- typed-interface facts
- compiler facts
- generic-template bodies and planning facts
- native dependency metadata
- target/profile/layout facts where the image is target-specific

The self-hosted compiler should port the logical model first, then choose an
efficient binary encoding for those typed sections.

Compiler facts in package images are durable facts only. They must preserve
package-boundary information such as public type layout, ABI/calling convention,
extern signatures, struct layout, C alias mappings, doctrine/trait satisfaction,
associated types, function/method generic type-parameter constraints, exported
symbol metadata, generic-template planning facts, and downstream constant facts.
Transient pass-local MIR/SSA facts stay in compiler IR fact tables unless a
package-image section explicitly defines a stable summary. See
[24-ir-memory-and-fact-model.md](24-ir-memory-and-fact-model.md).

## 4. Binary Load Requirements

The binary package image should be optimized for fast compiler loading:

- explicit magic and format version
- target/profile/data-layout compatibility facts when relevant
- section directory with stable section IDs, offsets, and lengths
- string/name table suitable for typed interning
- compact typed indexes instead of repeated string paths in hot sections
- section skipping so the loader only reads facts it needs
- validation before facts enter the compiler model
- diagnostics that name the package, section, offset/fact kind, and failed
  compatibility check

Normal compiler loading should not parse JSON, allocate inspection strings, or
reconstruct source text unless an explicit debug/inspection mode asks for that.

## 5. Inspection Requirements

Inspection output must be deterministic and stable enough for tests:

- sorted or explicitly ordered sections
- stable string escaping
- stable numeric formatting
- stable enum/tag names
- no hash-table iteration leakage
- optional detail levels, such as summary, interface, compiler-facts,
  generic-templates, and native metadata

`stark inspect-pkg` should be able to render JSON or text from a binary package
image. If explicit debug/test sidecar emission is added later, those sidecars
must be treated as views of the package image, not independent source-of-truth
artifacts.

## 6. Build-Time Artifact Selection

Recommended v1 behavior:

- normal package build: emit the binary package image
- local inspection: run `stark inspect-pkg` against the binary image
- package golden tests: compare `stark inspect-pkg` JSON/text output
- release distribution: ship the binary image and the inspector

Do not add a manifest-level artifact matrix or build-time sidecar emission for
v1. If repeated debugging later proves sidecars are worth it, add one explicit
debug/test flag then.

## 7. Compatibility Policy

Binary package images need a small compatibility policy before the host is
dropped:

- reject unknown required sections
- ignore unknown optional sections only when the section directory marks them as
  optional
- reject incompatible format versions
- reject incompatible target/profile/layout facts
- make bootstrap stages choose the package image matching their compiler stage

This replaces the current v1.1 assumption that compiler source and package image
format always evolve together without a durable format marker.

## 8. Work Items

- [x] Decide package-image format policy: binary for normal compiler loading,
      JSON/text through `stark inspect-pkg` for inspection/export.
- [ ] Finalize the public package-image contract: canonical binary extension,
      `stark inspect-pkg --format json|text`, deterministic inspection output
      conventions, and bootstrap policy for the legacy `.starkpkg.json` path.
- [ ] Design the durable binary format: header, magic, format version,
      target/profile facts, section directory, stable section IDs, durable
      compiler-fact policy, string/name tables, and typed index tables that
      cooperate with the compiler interner model.
- [ ] Implement the Stark package-image stack: binary writer/reader, logical
      models, builders, loaders, bridge code, shared codecs, and deterministic
      JSON/text inspection rendering from binary images.
- [ ] Add package-image diagnostics and compatibility behavior for malformed
      headers, unknown required sections, bad offsets/lengths, format
      mismatches, target/profile mismatches, and any temporary legacy JSON load
      path.
- [ ] Update tests so normal dependency loading uses binary images while
      golden/debug tests compare deterministic inspection output.
- [~] Route package-image outputs into the accepted
      `build/<profile>/<target-triple>/<stage>/pkg/` layout, with
      inspection views under `artifacts/` or an explicit caller output path.
      Current project library builds route legacy JSON package images to
      `pkg/<project>/` and preserve dependency linking through relative library
      references. Binary package images and inspection views under `artifacts/`
      remain open.

## 9. Documentation Work

- [x] Update `docs/Internals/PackageImage.md` from current-host JSON language to
      logical package-image model plus binary-load/inspection-output split.
- [ ] Update project/build docs once the binary package-image extension and
      `stark inspect-pkg` spelling are finalized.
- [ ] Update package-image test documentation to distinguish binary codec tests
      from JSON/text inspection golden tests.
