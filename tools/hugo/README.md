# Pinned Hugo Tool

The Stark website build is pinned to Hugo `v0.160.1`.

The build script expects the pinned Hugo executable at:

```text
tools/hugo/hugo
```

The executable is intentionally addressed by repository-relative path instead of relying on `PATH`, package managers, npm, Python, or a mutable system install. Once the binary is vendored, `scripts/build-site.sh` verifies that `hugo version` reports the version in `tools/hugo/VERSION` before building.

Pinned upstream artifact for Linux x64:

```text
https://github.com/gohugoio/hugo/releases/download/v0.160.1/hugo_0.160.1_linux-amd64.tar.gz
```

Do not replace the binary without updating `tools/hugo/VERSION` and the documented upstream artifact together.

Recorded checksums live in `tools/hugo/SHA256SUMS`.
