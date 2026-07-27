# Contributing to Stark

Stark is an early-stage, performance-focused systems language. Contributions
are welcome, but compiler correctness, explicit semantics, deterministic output,
and preservation of backend facts take priority over expanding the surface area.

## Before opening a change

Open an issue first for language-design changes, public standard-library APIs,
new dependencies, package-format changes, or work that changes an ABI. Small
bug fixes, tests, documentation corrections, and implementation work already
tracked in the repository can go directly to a pull request.

Do not put credentials, signing material, proprietary SDK files, personal paths,
or unredistributable binaries in an issue, patch, fixture, or build log.

## Build and test

Compiler development currently requires the exact .NET SDK 10.0.302 selected
by the repository's `global.json`; roll-forward is deliberately disabled so
local and release builds use the same SDK. Native output also uses the platform
development layer documented in
[`README.md`](README.md#requirements).

```bash
dotnet restore Stark.slnx
dotnet build -c Release Stark.slnx
dotnet test -c Release Stark.slnx
```

During development, run the narrowest relevant test project/filter first. Run
the complete applicable suite before requesting final review, and state any test
that could not run on your platform. Compiler changes should include a focused
regression test; lowering changes should verify the relevant MIR, SSA, LLVM, ABI,
package-image, and runtime behavior rather than checking only that source parses.

Website work uses the pinned Hugo version described in
[`tools/hugo/README.md`](tools/hugo/README.md). Hugo is website tooling and is
not part of the Stark SDK or release archives.

## Project conventions

- Preserve ownership, aliasing, layout, range, linkage, memory, and optimization
  facts through every compiler and package boundary.
- Never accept source that a later supported phase cannot lower.
- Keep release SDK resolution relocatable. Official `System.*` and `Vendor.*`
  packages must not depend on a contributor checkout or ambient `pkg-config`.
- Keep performance-sensitive changes allocation-conscious and benchmark material
  regressions against the relevant baseline.
- Prefer focused modules over files larger than roughly 5,000 lines when a
  coherent boundary exists.
- Follow the source and manifest conventions in the repository's Stark language
  skill and adjacent code.
- Preserve unrelated working-tree changes.

## Pull requests

Keep each pull request reviewable and describe:

- the problem and chosen design;
- user-visible, ABI, package, or performance effects;
- tests and benchmarks run, including target/host details;
- remaining limitations or follow-up work;
- third-party source, version, checksum, and license information for any added
  redistributed material.

By contributing, you agree that your contribution is licensed under the
repository's [MIT License](LICENSE).

## Release notes

GitHub Releases are the authoritative public changelog. Pull requests should
call out user-visible changes so release notes can be generated and reviewed.
