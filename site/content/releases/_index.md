+++
title = "Releases"
weight = 60
aliases = ["/downloads/"]
+++

Stark compiler binaries are not published yet. Until packaged releases are
available, use the source-build workflow in [Getting Started](/getting-started/).

The canonical public repository is currently:

- [gitlab.com/alexander.baggett/stark](https://gitlab.com/alexander.baggett/stark)

Once release artifacts are published, this page should point at the versioned
release stream:

- [GitLab releases](https://gitlab.com/alexander.baggett/stark/-/releases)
- [Git tags](https://gitlab.com/alexander.baggett/stark/-/tags)

Planned release artifacts include:

- platform-specific compiler archives
- matching standard-library package images
- source archives
- checksums
- release notes

For now, clone the repository and build from source:

```bash
git clone https://gitlab.com/alexander.baggett/stark.git
cd stark
dotnet build Stark.slnx
```
