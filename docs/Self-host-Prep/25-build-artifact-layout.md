# `.stark/build` Artifact Layout

Status: accepted direction for self-hosting prep.

This document locks in OQ-18/T05: Stark uses a formal `.stark/build/` layout
with profile, target, and compiler-stage directories. The goal is a stable,
predictable filesystem contract, not an artifact database.

## 1. Decision

Use option A from OQ-18:

```text
.stark/
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

After cutover, ordinary non-bootstrap builds may use the active compiler stage
selected by the driver. The layout remains stage-aware so tests, local bootstrap
runs, and future compiler development can still isolate outputs.

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
belong under `.stark/build/`.

## 4. Clean Semantics

The stable layout should support simple cleanup:

| Command shape | Deletes |
|---|---|
| clean profile | `.stark/build/<profile>/` |
| clean target | `.stark/build/<profile>/<target-triple>/` |
| clean stage | `.stark/build/<profile>/<target-triple>/<stage>/` |
| clean diagnostics/artifacts | selected `diagnostics/` or `artifacts/` subtrees |

Exact command spelling can be finalized with `stark build/run/test` work. The
important point is that the directory shape makes safe cleanup obvious.

## 5. Stdlib And Package Discovery

The stdlib discovery decision uses stage/build-local artifacts as the second
lookup tier. This layout defines where that tier lives:

```text
.stark/build/<profile>/<target-triple>/<stage>/stdlib/
```

The package image decision uses binary package images as the normal compiler
load path. Project/package images produced by the active build live under:

```text
.stark/build/<profile>/<target-triple>/<stage>/pkg/
```

`stark inspect-pkg` output is an inspection view and should normally be written
under `artifacts/` or a caller-requested output path, not beside the source.

## 6. Tests And Stage Comparison

Generated test runners, test binaries, and test-owned native objects live under
the active stage's `tests/` subtree.

Stage-comparison outputs should be explicit:

```text
.stark/build/<profile>/<target-triple>/stage1/artifacts/
.stark/build/<profile>/<target-triple>/stage2/artifacts/
```

Tests should compare requested artifacts from those directories, not scrape
source folders or rely on ad hoc temp paths.

## 7. Work Items

- [x] Decide OQ-18/T05: formalize `.stark/build/<profile>/<target-triple>/<stage>/`.
- [ ] Define the exact target-triple normalization used in path segments.
- [ ] Define the stage selector accepted by `stark build`, `stark run`, and
      `stark test`.
- [ ] Implement build-root selection for project, solution, and compiler
      bootstrap commands.
- [ ] Route package images into `pkg/`.
- [ ] Route stage-local stdlib artifacts into `stdlib/`.
- [ ] Route native objects/intermediates into `obj/` and final native outputs
      into `bin/`.
- [ ] Route generated test runners and test executables into `tests/`.
- [ ] Route targeted diagnostic and compiler artifact output into
      `diagnostics/` and `artifacts/`.
- [ ] Implement clean behavior for profile, target, stage, diagnostics, and
      artifacts.
- [ ] Update stdlib discovery to search the stage-local `stdlib/` path.
- [ ] Update package-image tests and stage-comparison tests to use the formal
      layout.
- [ ] Update user-facing project/build docs after command spelling and final
      artifact names are implemented.
