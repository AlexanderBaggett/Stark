# SDK Layout and Resolution

This document is the durable Stage0 contributor contract for relocatable Stark
SDK discovery. Release assembly details and unfinished cross-stage work remain
tracked under `docs/Self-host-Prep`.

## Identity and precedence

An SDK is identified by its canonical root and `<root>/sdk.json`. Resolution is
strictly:

1. `--sdk-root <root>`
2. `STARK_SDK_ROOT`
3. a bounded executable-relative candidate:
   `<canonical-executable-directory>/..` for the conventional
   `<sdk-root>/bin/stark[.exe]` release layout, or the executable directory
   itself for the generated repository development launcher

There is no current-project or project-ancestor SDK search. In particular, a
directory named `stdlib` or `vendor` above an application does not become an
SDK. Executable-relative discovery checks only the two explicitly supported
shapes above; it does not walk arbitrary compiler-parent folders. This is
required for builds to behave the same after installation and after the
application repository moves.

Stage-local package directories remain explicit build inputs. A development or
bootstrap stage that needs official source/package fallback selects a
`development` or `stage` SDK manifest rather than adding another search tier.

## Manifest kinds

- `release`: exact indexed binary packages and checksummed runtime payloads.
- `stage`: exact packages produced for a selected bootstrap stage.
- `development`: indexed packages may be combined with relative
  `developmentSourceRoots`. A source-only development SDK is valid.

Only a development manifest may declare source roots. Paths are relative to the
canonical SDK root and must remain inside it. `System.*` and `Vendor.*` stay
reserved; source in those namespaces is accepted only when its path is under a
manifest-declared development root.

The repository build writes a host development `sdk.json` containing
`stdlib/src`, `stdlib/templates`, and `vendor/src`, then generates the root
`stark`/`stark.cmd` launcher. The launcher selects that SDK only when the caller
has not already set `STARK_SDK_ROOT`. The generated manifest and launchers are
local build products, not release inputs.

## Project and compiler responsibilities

The compiler core owns SDK package/module resolution. Project mode forwards the
selected SDK root and does not turn nearby `stdlib` or `vendor` directories into
`-I` arguments. It may inspect manifest-declared development roots to apply the
owning source project's native metadata and to stamp build inputs, but module
selection remains in the compiler resolver.

The SDK module index gives exact package ownership. Package images remain the
authority for typed interfaces, generic templates, effects, ABI and target
facts, optimization hints, alias/memory facts, linkage, and native metadata.
The index must never reconstruct a reduced interface. Artifact validation is
lazy per selected package; `doctor` validates the complete advertised set.

## Incremental identity

Project `.stark-build-stamp` values include the canonical SDK root and a
SHA-256 hash of `sdk.json`. Manifest-declared development source/package inputs
are stamped as well. Dependency LLVM cache keys include the manifest path and
content hash in addition to compiler, target, options, closure, and inline-clone
seed identity. Any SDK switch or manifest edit therefore produces a cache miss.

## Accepted release layout

The public commands live under `bin/`; manifests and distribution assets remain
at the SDK root:

```text
<sdk-root>/
  bin/
    stark[.exe]
    <compiler runtime support files>
  sdk.json
  release.json
  stdlib/dist/<sdk-target>/...
  vendor/dist/<sdk-target>/...
  toolchain/...
  licenses/...
  docs/...
```

Users add only `<sdk-root>/bin` to `PATH`. Resolving the canonical executable
before selecting its parent keeps this working through command symlinks and
after relocating the complete SDK. The root-level repository `stark` and
`stark.cmd` launchers are deliberately limited to development manifests and
are not an alternate published archive shape.

The resolver follows paths and ownership recorded in `sdk.json`; directory
names are an archive convention, never a discovery algorithm. All runtime paths
are relative and relocation-safe. `release.json` records provenance; it does
not replace the runtime SDK manifest.

The staged compiler's assembly informational version must exactly equal the
release version written to `release.json` and `sdk.json`. Release publishing
sets that value explicitly and SDK assembly verifies it through the structured
`stark doctor --format json` report. This marketing version is deliberately
separate from the stable `stark-sdk-v1` compiler-compatibility line and binary
package-format version; changing a release label does not imply an SDK ABI or
package-format change.
