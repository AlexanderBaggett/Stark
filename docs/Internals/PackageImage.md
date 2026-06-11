# Stark Package Image

This document defines Stark's package image model for the `v1.1`
package-boundary work and records the self-hosting format direction.

The package image is a compiler-owned package-boundary artifact, not a narrow
hand-authored distribution manifest. It preserves structured package facts for
downstream compilation and inspection.

Current host status:

- the C# host emits and loads a binary `.starkpkg` container (STARKPKG magic,
  format version, Brotli-compressed canonical JSON payload); legacy
  `.starkpkg.json` files still load, and `--package-image-json` writes the
  indented JSON inspection sidecar on demand
- the JSON artifact is compiler-owned and not intended to be hand-authored
- current tests and tooling often inspect or diff that JSON directly

Self-hosting direction:

- the normal compiler load artifact becomes a binary package image
- deterministic JSON/text forms remain inspection and export views
- builds may emit binary, JSON, text, or any requested combination
- JSON/text sidecars are views of the package image, not independent sources of
  truth

Historical note:

- some internal type names and older comments still use `Manifest` because the
  feature started as a smaller package-manifest slice
- the current host file extension is `.starkpkg` (binary); `.starkpkg.json`
  remains the legacy/inspection JSON form
- self-hosting format work is tracked in `docs/Self-host-Prep/20-package-image-format.md`
- user-facing docs and tooling should treat package images as compiler-owned
  artifacts regardless of the concrete file format

## Goals

The package image exists so Stark can preserve enough package-boundary information to:

- load imported packages without original `.stark` source files
- type-check and lower imported declarations directly from structured compiler facts
- specialize imported generics without reparsing lossy source text
- preserve richer interprocedural optimization facts for future package-aware optimization work
- keep inspection output readable and diffable in Git

## Principles

The package image contract is:

- structured and compiler-owned rather than routinely hand-edited
- binary-loadable on the normal compiler hot path
- inspectable through deterministic JSON/text views
- sectioned so new compiler data can be added without flattening everything into one record
- loaded directly by the compiler from the binary artifact when structured
  sections are available
- compatible with explicit format/schema/version checks before the host is
  dropped

Stark intentionally does not treat the package image as a permanently stable
third-party interchange format in `v1.1`. The compiler and image format evolve
together in source control. The binary self-hosted format still needs enough
explicit compatibility data to reject mismatched package images cleanly across
bootstrap stages and releases.

## Artifact Shape

At the top level, a package image carries:

- package root identity
- library file name
- explicit module list

Each module entry keeps package and module boundaries explicit.
That boundary is part of the artifact itself, not something the loader has to infer from reconstructed source text.

The current primary module sections are:

- `source-surface`
- `typed-interface`
- `compiler-facts`
- `generic-templates`

## Section Roles

The package image separates authored surface from compiler-derived facts.

The roles are:

- authored source of truth:
  Stark `.stark` source files in the producing package
- compiler-emitted source-surface section:
  a structured publication of the authored import, re-export, function, type, global, and alias surface
- compiler-emitted typed-interface section:
  structured type and signature facts used directly by downstream loading and type checking
- compiler-emitted compiler-facts section:
  effect, ABI, layout, ownership, interprocedural call, and other lowering-relevant facts that should survive package publication
- compiler-emitted generic-template section:
  structured template bodies and planning facts used for imported specialization and lowering
- compiler-only compatibility data:
  temporary legacy flat fields and bridge-oriented fallback content that still exist only while the older reconstruction path is being retired

No section in the package image is intended to be the normal place a user writes package APIs by hand.
Users author Stark source; the compiler emits the package image.

## Native Dependency Metadata

Package images carry package-owned native dependency metadata. This lets an
interop package own its C shim and link requirements instead of making every
downstream user repeat a long command line.

The current package-author CLI surface is:

```bash
compiler Raylib.stark --emit-lib \
  -o dist/libRaylibStark.a \
  --package-image-output dist/pkg/libRaylibStark.starkpkg \
  --native-source RaylibNative.c \
  --native-pkg-config raylib
```

The package image records those facts under its top-level native dependency
section. Downstream executable builds that import the package image gather those
facts, compile package-owned native sources, add package-owned library search
directories, and pass package-owned native libraries/link arguments to the final
link.

When a dependency is not available through `pkg-config`, package authors can
spell the same information explicitly with `--native-include-dir`,
`--native-library-dir`, `--native-library`, and `--native-link-arg`. This keeps
local and vendored native builds supported without making downstream users repeat
those details.

A future source-level Raylib package surface may look like this:

```stark
package Raylib
{
    native source "RaylibNative.c";
    native library "raylib";
    native library "GL";
    native library "m";
}
```

The source syntax is still planned. The implemented surface is the package-image
metadata and CLI path above.

Native dependency facts should cover only explicit package-owned interop needs:

- native shim source files
- include directories
- library search directories
- libraries
- platform-specific system libraries and link arguments
- optional discovery names such as `pkg-config` packages

This is not an arbitrary build-script mechanism. The toolchain should gather
transitive package metadata, compile package-owned native shims, de-duplicate
link inputs deterministically, and report missing native paths with the package
name and exact missing item. When the final native linker reports that a named
system library such as `raylib` or `GL` cannot be found, the compiler also emits
a Stark diagnostic that points users toward installing the library or adding a
`-L` / `--native-library-dir` search path. When `pkg-config` cannot resolve a
package-owned discovery name, the compiler reports the package name and suggests
installing that native package, setting `PKG_CONFIG_PATH`, or using explicit
native metadata instead.

## Loading Rules

The loader should prefer the structured package image sections over synthetic source reconstruction.

That means:

- `typed-interface` facts should beat stringly reconstructed declarations
- `compiler-facts` should beat rediscovery from fallback bridge text
- `generic-templates` should beat reparsing reconstructed generic bodies when the typed template summary is sufficient
- imported generic, alias, doctrine, and trait handling should rely on structured package-image facts even if reconstructed bridge parse trees are empty or corrupted
- the synthetic source bridge remains only as a compatibility path for legacy manifests or tooling flows that explicitly ask for reconstructed source text

Direct compiler loading is the primary model.
Synthetic-source reconstruction is temporary bridge behavior.

## Generic Template Publication Rules

The `generic-templates` section is package-boundary specialization material,
not a dump of every generic body in a package.

Publication starts from generic function or method template bodies where all of
the following are true:

- the declaration is published at package boundary visibility (`public` or `export`)
- the declaration has a real body
- the typed function signature is generic, including methods that are generic by
  virtue of their enclosing generic type

The compiler then publishes the package-private generic helper closure required
by those API-visible templates. This lets downstream packages specialize a
public generic body that calls internal generic helpers without exposing every
unrelated internal generic body.

That means:

- unrelated `module` and `internal` generic functions stay package-private
- internal generic helpers referenced by published generic templates may receive
  template entries as specialization material
- methods on `module` or `internal` types stay package-private with their
  containing type
- non-generic functions and methods do not get generic template entries
- declarations without bodies do not advertise a published generic template body

The typed-interface `HasGenericTemplateBody` flag remains an API-surface marker.
It means the declaration itself publishes a package-boundary generic body, not
merely that the helper closure carries a package-private template entry for
downstream specialization.

## Optimization-Ready Template Representation

The typed template-body representation is intended to support future
package-aware optimization passes, not just the minimum information needed to
reconstruct or lower imported code.

In addition to the structured statement and expression tree, the package image
preserves optimizer-relevant facts such as:

- top-level statement count and estimated body cost
- semantic summaries, including effective kind, memory effects, and called-function sets
- structural optimization summaries for wrapper-like and terminal-control-flow helpers
- per-call memory and capture facts
- typed local declaration facts
- typed conversion facts
- typed direct-call, member-call, and field-access targets
- typed object-creation and initializer-member facts
- typed enum constructor, enum call, enum value, enum pattern, and aggregate pattern facts
- `try` propagation facts (ordinal-keyed roles, payload types, and `from` funnel
  resolution per `try` expression), so downstream specialization lowers `try` without
  re-type-checking the imported body

Published enum types additionally carry their `[Ok]`/`[Err]` propagation roles and
`from` funnel markers on every variant, in the source-surface, typed-interface, and
compiler-facts sections alike, so imported enums stay `try`-propagatable.

That richer surface lets future cross-package inlining and other package-aware
optimizations reason about imported generic bodies from structured package-image
data instead of treating the template section as a minimal codegen-only bridge.

## Why This Is Not Just A Manifest

A narrow package manifest would normally answer only questions like:

- what is the root module
- what file should the linker consume
- what top-level modules are exported

Stark's package image intentionally carries much more:

- authored source-surface structure
- typed interfaces
- compiler facts
- generic template bodies and specialization-planning summaries

That richer shape is what makes package-boundary generic specialization and structured downstream loading possible without shipping original source files.

## CLI Surface

The current user-facing commands are:

- `--emit-lib`:
  emits a static library plus a package image; `--package-image-output <path>`
  can route the image away from the library, in which case the image records a
  relative library reference for downstream linking
- `--emit-pkg` or `--emit-package`:
  currently emits the host JSON package image without linker or archiver steps;
  self-hosting should let builds choose binary, JSON, text, or any requested
  combination
- `--inspect-pkg` or `--inspect-package`:
  validates a package image and renders deterministic text or JSON inspection
  output

## Compatibility Note

The repository still contains some legacy uses of the word `manifest` in internal identifiers and compatibility paths.
That legacy naming does not change the intended model:

- `.starkpkg` is the current host's compiler-owned binary package image;
  `.starkpkg.json` is its deterministic JSON inspection form
- binary package images are the self-hosted compiler's normal load path
- JSON/text inspection output remains compiler-owned and deterministic
- direct structured loading is the primary path
- legacy manifest-style reconstruction is temporary bridge behavior, not the semantic source of truth for imported package handling
