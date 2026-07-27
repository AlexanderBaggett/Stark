# Stark Package Image

This document defines Stark's package image model for the `v1.1`
package-boundary work and records the self-hosting format direction.

The package image is a compiler-owned package-boundary artifact, not a narrow
hand-authored distribution manifest. It preserves structured package facts for
downstream compilation and inspection.

Current host status:

- the C# host emits and loads a binary `.starkpkg` container (STARKPKG magic,
  exact format version, section directory, required `STRS` string table,
  required `PINF` package identity/target/profile facts, and required `MANF`
  section with a Brotli-compressed canonical JSON package model); legacy
  `.starkpkg.json` files and the earlier v1 non-sectioned binary wrapper still
  load, and `--package-image-json` writes the indented JSON inspection sidecar
  on demand
- the JSON artifact is compiler-owned and not intended to be hand-authored
- current tests and tooling often inspect or diff that JSON directly

Self-hosting direction:

- the normal compiler load artifact becomes a binary package image
- deterministic JSON/text forms remain inspection and export views
- builds may emit binary, JSON, text, or any requested combination
- JSON/text sidecars are views of the package image, not independent sources of
  truth

The Stage1 implementation keeps the stable
`Compiler.Mir.PackageImage` module as a small compatibility facade. Package
models live in `PackageImage.Models`; fixed binary and type codecs live under
`PackageImage.Shared`; logical and legacy writers live under
`PackageImage.Builder`; validated materialization lives under
`PackageImage.Loader`; and deterministic rendering and file adapters live under
`PackageImage.Inspection`. Focused modules depend on models and shared codecs,
never back through the facade. This preserves an acyclic hot path and lets the
compiler load only the package families it consumes.

The synthetic-source bridge is intentionally not part of the ordinary loader.
Stage1 has materialized the source-surface bridge graph, but source-document
reconstruction remains a separate compatibility feature until the Stage1
frontend owns a loaded-module/source-document model. Direct typed-interface,
compiler-fact, ABI, layout, ownership, alias, range, native-metadata, and
generic-template loading does not wait on that bridge.
The focused Stage1 bridge module now performs one `MANF` parse to render the
effective import/re-export prefix, module identity, and source type aliases;
later declaration/body reconstruction extends that boundary without moving
bridge behavior back into the loader.

Stage1 now also has a first source-free imported-template MIR path. A typed
template whose complete body is an ordered top-level sequence of empty or pure
constant-expression statements followed by one integer or boolean expression
return lowers directly from the materialized generic-template graph. The same
sequence may be wrapped in one terminal top-level block when its body is flat.
Discarded
constant roots are evaluated for validation and conversion-row consumption but
emit no runtime work. Bounded
nested literal/unary/binary/comparison-chain/conditional/comptime/conversion
arithmetic, comparisons, and boolean logic fold during import lowering to one
MIR constant, avoiding a runtime operator tree. Integer folding includes
checked arithmetic, bounded power, signed wrapping and saturating arithmetic,
signed wrapping negation, bitwise operations, and checked shifts. Signed
saturation clamps to the published ranged result type rather than only its
storage width. Unsigned wrapping/saturation remains on compatibility fallback
when the signed MIR fact carrier cannot represent the complete unsigned result;
representable unsigned bitwise and shift results retain their unsigned return
contract and exact range. Operator decoding and evaluation live in the focused
`Compiler.Mir.ImportedTemplateScalarOperators` module so the direct lowerer
does not accumulate another monolithic implementation. Conversions consume ordered
published conversion side rows, require exact target scalar/range identity, and
use Stage0-compatible two's-complement width normalization before rechecking the
target's inclusive range. Conversion ordinals remain in source order across
all statement roots. The path consumes the canonical Stage0 schema
(lowercase type kinds, `Name`-backed unary/binary operators, operator-row
comparison chains, and inferred expression result types) rather than requiring
normalized result annotations. The path
also consumes ordered immutable scalar local constants and later `name`
references without materializing runtime storage. Each local statement must
match one contiguous `const` declaration side row, its exact published type,
`local` storage, and `immutable-binding` provenance. Initializers are evaluated
before binding, duplicate or unresolved names fail closed, and grouped,
mutable, storage-backed, or more-than-64-local bodies retain compatibility
fallback. The focused `Compiler.Mir.ImportedTemplateScalarLocals` module keeps
the environment in bounded stack arrays, caches one stable hash per declared
name, and performs exact text comparison on hash matches with two reusable
scratches; the terminal value still emits as one constant with singleton MIR
and LLVM range facts.
Independent scalar `var` declarations now use the same environment when their
storage is `stack` or `register`, including declarations without an initializer.
The environment carries a definite-initialization bit alongside exact type,
signedness, mutability, and value facts. Name reads and compound updates require
that bit; a mutable local's first ordinary `=` or separately published `init =`
write establishes it. Direct-name assignments consume
both the published statement name and separate target-expression tree, require
a mutable target, and implement `=` plus every grammar-defined checked,
wrapping, saturating, and bitwise compound assignment. Saturating updates clamp
to the declared ranged local type; every update must fit that type before the
environment changes. Heap/arena, grouped, indirect, or
otherwise observable storage retains compatibility fallback. Consequently the
entire scalar statement sequence can disappear into one terminal MIR constant
instead of relying on later SROA or memory optimization.
The direct lowerer preflights the entire per-template statement/expression shape, refuses overflow
and any unconsumed call, ownership, layout, enum, or bound-operation
family, requires the graph result type, signedness, and exact folded range to
satisfy the specialized function return contract supplied by its caller, and
rejects scalar-inapplicable caller fact families rather than silently dropping
LLVM inputs. Statement and expression spans are validated once and then walked
by direct row offset, avoiding whole-graph rescans on wider bodies. The flat
block boundary exploits the loader's contiguous direct-child batch, preserving
source-ordered conversion facts without allocating a parent index. Deeper
nesting remains on compatibility fallback. It reserves
all dense output tables only after those checks. It records the
folded value's exact range on both the MIR value and the function-return fact
table, so LLVM definitions and downstream call sites receive that fact without
rediscovering it from synthetic source. Unsupported bodies continue through the
compatibility bridge; malformed graphs and fact-table misalignment do not
silently fall back.

`Compiler.Mir.ImportedTemplateSpecialization` now supplies the package-backed
specialization boundary around that scalar lowerer. It resolves the exact
qualified resolved name and overload key, joins the template's base qualified
name to exactly one compiler function-effect row, and validates backend-mode
agreement before any MIR table changes. The adapter carries purity and memory,
progress, unwind, hot/cold, strict-FP, inline or opaque optimization mode,
fast/tail calling convention, and no-recurse facts into the numbered LLVM
definition alongside the exact return range. Missing, duplicate, or
inconsistent package facts fail transactionally. Routing this adapter from the
general package-import driver still depends on that driver owning concrete
type/comptime substitution and imported-package selection.

Historical note:

- some internal type names and older comments still use `Manifest` because the
  feature started as a smaller package-manifest slice
- the current host file extension is `.starkpkg` (binary); `.starkpkg.json`
  remains the legacy/inspection JSON form
- remaining self-hosting format work is maintained in the source repository's
  internal compiler work tracker and is not part of the release package-format
  contract
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

The host package image also records top-level target facts when the producing
compilation has an explicit or detected target. Those facts include the target
triple, LLVM data layout when known, CPU/features, relocation/code model, C data
model, and aggregate pointer layout. The binary `PINF` section duplicates the
same package identity, target, and profile facts through typed string-table
indexes and is checked against the `MANF` payload during load. Downstream
loading rejects target-specific package images whose recorded facts do not match
the active compilation target before ABI/layout facts enter backend lowering.

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
interop package own its optional C shims and link requirements instead of making
every downstream user repeat a long command line.

There are two consumption modes:

- an official `Vendor.*` release package is already resolved and bundled by the
  target-specific SDK; its `sdk.json` descriptor supplies checksummed,
  SDK-relative package/native artifacts and ordered link facts without
  `pkg-config`, user configuration, or `-I`/`-L` flags
- a custom or source-built native package may retain discovery inputs such as
  `pkg-config`, explicit paths, or user-configured fallbacks while its author
  produces a package image

The current package-author CLI surface is:

```bash
compiler Raylib.stark --emit-lib \
  -o dist/libRaylibStark.a \
  --package-image-output dist/pkg/libRaylibStark.starkpkg \
  --native-pkg-config raylib
```

The package image records those facts under its top-level native dependency
section. Downstream executable builds that import the package image gather those
facts, compile package-owned native sources when present, add package-owned
library search directories, and pass package-owned native libraries/link
arguments to the final link.

When a dependency is not available through `pkg-config`, package authors can
spell the same information explicitly with `--native-include-dir`,
`--native-library-dir`, `--native-library`, and `--native-link-arg`. This keeps
local and vendored native builds supported without making downstream users repeat
those details.

A future source-level Raylib package surface may look like this:

```stark
package Raylib
{
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

That diagnostic applies to a custom or development package that still declares
native discovery. It is not a valid fallback for an installed official SDK
package. If an advertised official package lacks a required archive or its
checksum does not match, the compiler reports an SDK-integrity error and points
to `stark doctor --strict`; it must not silently search the host for a substitute.

Package ABI facts and the compiled wrapper archive are a matched pair. Whenever
the compiler's target C ABI classification changes, affected official package
images and archives must be regenerated before SDK publication. This is
observable even when the source signature is unchanged: on AArch64 a four-byte
integer-like C struct has an exact-width `i32` return carrier but a rounded
`i64` parameter carrier. Package serialization therefore preserves return and
physical parameter carriers separately. A distinct parameter-carrier list is
serialized even when it contains only one value (`[i64]` here); treating that
field as multi-value-only would discard an ABI fact and make a downstream
package emit the wrong native declaration.

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

Stage1 now owns that compatibility result as a loaded source document: the
rendered bytes are retained beside the compilation-unit token and declaration
tables, syntax diagnostics reject the document, and parsed header rows preserve
the `export` bit on re-export imports. This is deliberately not a second source
of compiler truth. The loaded typed-interface, compiler-fact, ownership,
layout, ABI, and generic-template graphs remain canonical downstream.

Global constants make the boundary especially important. The compatibility
source uses neutral, type-correct literals (`0`, `false`, `""`, `null`, or a
recursively shaped fixed array) only so parsing and name resolution can proceed.
The actual scalar values, integer ranges, aggregate elements, and types remain
in typed constant-initializer rows for CTFE and LLVM lowering. Unsupported
aggregate placeholder shapes fail the bridge instead of inventing a value.

Generic-template body matching follows the same structured-first rule. A
template is identified by qualified source name plus overload key. The bridge
first uses the published overload key from the effective typed interface and
otherwise derives the Stage0-compatible key from canonicalized source
parameter types. Legacy `BodyText` may be attached to a matching top-level
function or method only when that template has no `TypedBody`; a present typed
body always wins, even if stale legacy text is also present. Duplicate template
identities reject the bridge instead of choosing an order-dependent body.

The overload-key builder reuses one owned scratch buffer across declarations
and canonicalizes qualifier order before removing whitespace. This keeps the
compatibility import path allocation-bounded while matching Stage0 identity
semantics. For structured typed bodies, a bounded statement/expression/pattern
walk decides whether the compatibility parser needs body text at all. Bodies
whose operations are consumed directly by imported-template lowering remain
declarations. The first source-required form renders an exact single-return
direct call by matching its published ordinal and selecting the Stage0 target
name precedence (`QualifiedSourceName`, then `QualifiedTemplateName`, then
`QualifiedResolvedName`). The recursive expression subset also joins field
access and non-generic member-call ordinals, recovers the member spelling from
its qualified source name, supports nested name/literal arguments, and uses an
object-creation row's exact authored `ExpressionText` when the expression has
no reconstructed arguments. It also reconstructs enum constructor/call/value
expressions, synthesized named-type constructor/initializer and arena
allocations, and generic/comptime direct and member calls. Direct calls retain
Stage0's parsing optimization: pure type arguments remain inferred, while a
comptime argument makes the complete explicit generic list necessary. Typed
bodies render ordered local declarations, empty statements, expression
statements, assignments, breaks, continues, and returns into one reused owned
scratch buffer. Local declarations preserve their storage class, mutability,
structured named type, and initializer. Object-initializer expressions retain
member order and require an exact member/value count before rendering.
Bounded recursive statement rendering also reconstructs typed `if`/`else`
trees, labeled `while` loops, counted `for` loops, allocation-free traversal
loops, explicit blocks, and switches. Loop behavior, ordered loop contracts,
labels, traversal bindings, guards, case order, and recursive child statements
come directly from typed rows. Condition and switch patterns cover discard,
capture, literal, inclusive range, exact list, enum, aggregate, named-field,
positional, and whole-value capture forms; enum/aggregate member names are
joined through exact published ordinals.

Structured type rendering covers scalar, integer-range, floating, raw-pointer,
fixed-array, slice, dynamic, named/generic, associated, dyn-trait,
function-pointer, and closure forms. It retains qualifier order, source aliases,
symbolic comptime arguments, callable kind/safety/ABI/tail facts, bounded raw
pointer counts, closure storage/capability, and deduplicated overlap/same/dead
memory contracts without temporary collections. The expression renderer also
handles array and object initializers, assignment, conversion, `try`, unary and
binary operators, comparison chains, conditionals, `comptime`, layout queries,
closure calls, indexing, and dyn-trait construction around ordinal-backed
operations. Malformed counts, duplicate operation ordinals, or incomplete
callable facts fail closed. In every case, operation/type rows remain canonical
for specialization and LLVM lowering and are never replaced with legacy body
text. The compatibility bridge remains only until direct imported-template
lowering consumes every structured typed-body family.

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
- `stark build --package-image-json`:
  for project library builds, writes the explicit JSON inspection view under
  `build/<profile>/<target>/<stage>/artifacts/pkg/<project>/` while keeping the
  binary package image under `pkg/<project>/`
- `--emit-pkg` or `--emit-package`:
  emits the binary package image without linker or archiver steps; JSON/text
  sidecars are explicit inspection/export views rather than normal build outputs
- `inspect-pkg` or `inspect-package`:
  validates a package image and renders deterministic text or JSON inspection
  output selected with `--format text|json`; the older `--inspect-pkg` and
  `--inspect-package` flag forms remain accepted during migration

## Compatibility Note

The repository still contains some legacy uses of the word `manifest` in internal identifiers and compatibility paths.
That legacy naming does not change the intended model:

- `.starkpkg` is the current host's compiler-owned binary package image;
  `.starkpkg.json` is its deterministic JSON inspection form
- binary package images are the self-hosted compiler's normal load path
- JSON/text inspection output remains compiler-owned and deterministic
- direct structured loading is the primary path
- legacy manifest-style reconstruction is temporary bridge behavior, not the semantic source of truth for imported package handling

The Stage1 module split was verified against the pre-split implementation with
the same deterministic sectioned-MIR fixture. Both implementations emitted the
same 124-byte image with SHA-256
`4897f97f7db9eea83207f0134adf217e744e441a761a304ca71756e8660fd5b6`.
Logical-image coverage separately exercises exact `STRS`/`PINF`/`MANF`
directory facts, deduplicated string indexes, target/backend facts, compressed
payload copying, and all materialized graph families.

Stage1 package emission uses standards-compliant uncompressed Brotli
meta-blocks for `MANF`; Stage1 decodes that bounded subset directly with a
single pass over the validated image range, without first allocating a second
compressed-payload buffer. General Brotli streams produced by Stage0's
optimal compressor remain on the host decompression handoff until a full
Stage1 decoder or an explicitly shipped native decoder lands.

The focused Stage1 source bridge renders effective imports/re-exports, module
identity and opaque backend policy, aliases, and declaration-only
source-surface functions from one parsed manifest. Function rendering retains
link name, source-spellable opaque backend mode, strict floating-point policy,
hot/cold and inline preferences, unsafe/FFI ABI, varargs/tail, bounded raw
pointer counts, `dead_on_return`, generic trait constraints, thread laws, value
contracts, and named or bounded disjoint/overlap/same groups. Count-only
placeholder objects are rejected instead of silently erasing semantic, range,
or alias inputs before parsing or LLVM lowering.

The same one-parse bridge reconstructs source-spellable immutable/mutable
static globals and simple structs, records, traits, and enums. Type reconstruction
retains opaque optimization mode, struct layout, pack/alignment, generic
parameters, record primary constructors, associated aliases, implemented-trait
bases, dyn-trait identity, type/field thread-safety laws, explicit field
offsets, visibility, and source field types. Primary-constructor field names are
filtered with direct scans over the parsed rows rather than a temporary hash
set.
Enum reconstruction preserves positional and named payload types, `[Ok]`/`[Err]`
roles, and `from` conversion funnels, including an exact check that the funnel
payload agrees with the absorbed error type. Struct/record constructors and
destructors retain their authored bodies, while struct/record/trait method
headers retain the same ABI, performance, generic, thread-law, value-contract,
and alias-region facts as top-level callables. Method bodies remain sourced
from typed generic-template facts rather than guessed from a header; enum
methods, constants, and malformed or count-only member payloads fail closed.
The bridge never substitutes a weaker declaration that would change LLVM
layout or optimization inputs.
