# Release Process

This document defines the minimum release, changelog, and upgrade-note discipline for Stark `v1.0`.

It is intentionally lightweight. The goal is to make release behavior predictable without inventing a heavy release-management system before `v1.0`.

## Version Numbers

Stark release tags use `vMAJOR.MINOR.PATCH`.

Examples:

- `v1.0.0`
- `v1.0.1`
- `v1.1.0`

Rules:

- `PATCH` is for bug fixes and release-readiness corrections that preserve the documented `v1.0.x` baseline
- `MINOR` may add backward-compatible language, tooling, or stdlib surface after `v1.0`
- `MAJOR` is required for intentional breaking changes to documented syntax, lowering, packaging, or the public standard-library baseline

Pre-release builds may use suffixes such as `-alpha.1`, `-beta.1`, or `-rc.1` when needed.

## Release Tagging

The minimum release flow for a tagged build is:

1. Update [CHANGELOG.md](../CHANGELOG.md) by moving the relevant entries out of `Unreleased` into a versioned heading.
2. Update [docs/ReleaseNotes.md](./ReleaseNotes.md) with any release-specific notes that deserve a short narrative summary.
3. Confirm that [docs/V1ReleaseSubset.md](./V1ReleaseSubset.md), [docs/StandardLibraryBaseline.md](./StandardLibraryBaseline.md), and [docs/UnsupportedFeatures.md](./UnsupportedFeatures.md) still match the shipped compiler behavior.
4. Create a git tag named `vMAJOR.MINOR.PATCH`.
5. Publish the matching compiler artifact and the matching packaged `System` stdlib artifact for that tag.

## Changelog Template

Each release entry in [CHANGELOG.md](../CHANGELOG.md) should follow this shape:

```md
## v1.0.0 - YYYY-MM-DD

### Upgrade Notes

- Short migration note when needed.

### Added

- New user-facing capability.

### Changed

- Behavior change that is not purely a fix.

### Fixed

- Bug fix or compatibility correction.

### Removed

- Removed surface or compatibility alias.
```

If a section has no entries, use `- None.` or omit the section when the release is small.

## Upgrade Notes

Breaking or user-visible migration changes must be called out in two places:

- the `Upgrade Notes` section for the release in [CHANGELOG.md](../CHANGELOG.md)
- the matching release entry in [docs/ReleaseNotes.md](./ReleaseNotes.md) when the change needs more explanation

Upgrade notes should include:

- what changed
- who is affected
- the old form and the new form when code changes are required
- links to the authoritative docs when relevant

## Scope Of The Process

This process is the `v1.0` baseline, not the final long-term release story.

It deliberately avoids:

- automated publishing requirements
- multi-branch support policy
- separate compiler and stdlib version streams before the manifest format supports that cleanly

If Stark later needs a more complex release process, this document should be expanded rather than replaced with ad hoc instructions.
