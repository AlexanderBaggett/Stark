# `build` Artifact Layout

Status: accepted direction for self-hosting prep.

This document locks in OQ-18/T05: Stark uses a formal `build/` layout
with profile, target, and compiler-stage directories. The goal is a stable,
predictable filesystem contract, not an artifact database.

## 1. Decision

Use option A from OQ-18:

```text
build/
  <profile>/
    <target-triple>/
      <stage>/
        bin/
        obj/
        pkg/
        stdlib/
        native/
        tests/
        diagnostics/
        artifacts/
```

Where:

- `<profile>` is the build profile, such as `dev` or `release`.
- `<target-triple>` is the resolved target triple, such as
  `x86_64-unknown-linux-gnu`.
- `<stage>` is the compiler stage that produced the artifacts.
- `bin/` contains final executable/library outputs.
- `obj/` contains object files and other native intermediate files.
- `pkg/` contains package images for the project/package being built.
- `stdlib/` contains stage-local `System` package artifacts used during
  bootstrap.
- `native/` contains copied or generated native dependency artifacts when the
  build owns them.
- `tests/` contains generated test runners and test executables.
- `diagnostics/` contains targeted diagnostic output requested by tests or
  debug commands.
- `artifacts/` contains selective compiler artifacts such as MIR, SSA, LLVM
  text, package inspection output, and stage-comparison output.

The default stage names for self-hosting are:

| Stage | Meaning |
|---|---|
| `stage0` | Existing C# host compiler. |
| `stage1` | Stark compiler built by Stage0. |
| `stage2` | Stark compiler built by Stage1. |

After self-hosted release adoption, ordinary non-bootstrap builds may use the
active compiler stage selected by the driver. The layout remains stage-aware
so tests, local bootstrap runs, and future compiler development can still
isolate outputs, including explicit Stage0 maintenance builds.

## 2. Build Root

The build root is the command's owning workspace:

- project root for a single-project command
- solution root for a solution command
- repository root for Stark compiler bootstrap commands

Do not scatter generated files into source directories. Source folders may still
contain checked-in source, manifests, docs, and explicit fixtures.

## 3. Non-goals

- Do not add a manifest-level artifact matrix.
- Do not make each project invent its own build layout.
- Do not hide stage artifacts in global user cache directories.
- Do not require network or global package discovery.
- Do not make broad logs/metrics/timings part of the layout contract.
- Do not store compiler cache internals in `bin/`, `pkg/`, or `stdlib/`.

Separate top-level `.stark/cache/` and `.stark/packages/` directories may still
exist for cache and package-manager work, but self-hosting bootstrap artifacts
belong under `build/`.

## 4. Clean Semantics

The stable layout supports simple cleanup through `stark clean`:

| Command shape | Deletes |
|---|---|
| `stark clean profile` | `build/<profile>/` |
| `stark clean target --target <triple>` | `build/<profile>/<target-triple>/` |
| `stark clean` or `stark clean stage --target <triple>` | `build/<profile>/<target-triple>/<stage>/` |
| `stark clean diagnostics --target <triple>` | `build/<profile>/<target-triple>/<stage>/diagnostics/` |
| `stark clean artifacts --target <triple>` | `build/<profile>/<target-triple>/<stage>/artifacts/` |

`target`, `stage`, `diagnostics`, and `artifacts` scopes use the explicit
`--target <triple>` value or the detected default target. `profile` cleanup does
not require target discovery.

## 5. Stdlib And Package Discovery

The stdlib discovery decision uses stage/build-local artifacts as the second
lookup tier. This layout defines where that tier lives:

```text
build/<profile>/<target-triple>/<stage>/stdlib/
```

Project builds add this directory to module/package search for the active
profile, target, and stage. After this stage-local tier, source-tree
development builds search the nearest repo `stdlib/dist` package image
directory and then `stdlib/src` source directory; installed builds then search
bundled stdlib artifacts next to the active compiler distribution. Producing the
stdlib package/source artifacts that live in the stage-local tier remains part
of the stdlib packaging work.

The package image decision uses binary package images as the normal compiler
load path. Project/package images produced by the active build live under:

```text
build/<profile>/<target-triple>/<stage>/pkg/
```

`stark inspect-pkg` output is an inspection view and should normally be written
under `artifacts/` or a caller-requested output path, not beside the source.
Project builds keep normal dependency loading binary-only; when
`stark build --package-image-json` is requested for library package images, the
derived JSON inspection view is written under:

```text
build/<profile>/<target-triple>/<stage>/artifacts/pkg/<project>/
```

## 6. Tests And Stage Comparison

Generated test runners, test binaries, and test-owned native objects live under
the active stage's `tests/` subtree.

Stage-comparison outputs should be explicit:

```text
build/<profile>/<target-triple>/stage1/artifacts/
build/<profile>/<target-triple>/stage2/artifacts/
```

Tests should compare requested artifacts from those directories, not scrape
source folders or rely on ad hoc temp paths.

## 7. Work Items

- [x] Decide OQ-18/T05: formalize `build/<profile>/<target-triple>/<stage>/`.
- [x] Define the build-layout command contract: target-triple normalization,
      accepted stage selectors for `stark build`, `stark run`, and `stark test`,
      and build-root selection for project, solution, and compiler bootstrap
      commands.
- [~] Implement artifact routing for the formal layout: final executable/library
      outputs route to `bin/<project>/`, test executables and generated runners
      route to `tests/<project>/`, saved native intermediates route to
      `obj/<project>/`, and project library package images route to
      `pkg/<project>/` under `build/<profile>/<target-triple>/stage0/`, with
      explicit package-image JSON inspection views routed to
      `artifacts/pkg/<project>/`. Stdlib artifact generation/routing,
      diagnostics, other requested compiler artifacts, and actual Stage1/Stage2
      execution remain open.
- [x] Implement clean/discovery behavior for the formal layout, including
      profile/target/stage/artifact cleanup and stdlib discovery from the
      stage-local `stdlib/` path.
- [ ] Update package-image tests, stage-comparison tests, and artifact
      inspection tests to use the formal layout.
- [ ] Update user-facing project/build docs after command spelling and final
      artifact names are implemented.
