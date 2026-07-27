+++
title = "Releases"
weight = 60
aliases = ["/downloads/"]
+++

Stark compiler binaries are not published yet. Until packaged releases are
available, use the source-build workflow in [Getting Started](/getting-started/).

The canonical public repository is:

- [github.com/AlexanderBaggett/Stark](https://github.com/AlexanderBaggett/Stark)

Once release artifacts are published, this page should point at the versioned
release stream:

- [GitHub Releases](https://github.com/AlexanderBaggett/Stark/releases)
- [Git tags](https://github.com/AlexanderBaggett/Stark/tags)

Planned release artifacts include:

- one complete 64-bit SDK archive per supported OS/architecture
- the matching compiler, System and Vendor package images, and private backend
- offline documentation, examples, licenses, and optional installers
- per-archive SHA-256 checksums and release notes
- GitHub-generated source archives for the exact release tag

For now, clone the repository and build from source:

```bash
git clone https://github.com/AlexanderBaggett/Stark.git
cd stark
dotnet build Stark.slnx
```

Source builds use the exact .NET SDK selected by the repository's
`global.json`.
