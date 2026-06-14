# System.Toml

Status: WIP, decision locked.

This document records the self-hosting decision to add a reusable
`System.Toml` library rather than a manifest-only parser.

## 1. Decision

Add `System.Toml` as the blessed TOML parser/emitter for Stark.

This resolves OQ-10 as option A:

- `Stark.toml`, `Stark.solution.toml`, `Stark.user.toml`, and future user config
  files stay TOML.
- The self-hosted compiler does not carry forward the host's private
  `SimpleToml` parser as the long-term model.
- The manifest reader in the project driver uses `System.Toml` plus a typed
  manifest decoding layer.
- The parser/emitter is reusable by users and tooling, not compiler-only.

## 2. Standard Baseline

`System.Toml` should target a released TOML standard, not an undocumented Stark
subset. As of this decision, the current released TOML spec is TOML 1.1.0, and
TOML 1.0.0 remains an important compatibility baseline for existing tools.

Implementation should make the supported TOML version explicit in diagnostics
and docs. If Stark chooses to accept only a staged subset during bootstrap, that
subset must be tracked as temporary implementation work under this document, not
as the design target.

## 3. Scope

`System.Toml` should provide:

- parser from text or bytes
- emitter/writer for deterministic TOML output
- typed TOML value model
- source spans for parsed values
- diagnostics with file, line, column, and parse context
- table/key lookup helpers
- typed projection helpers for manifests
- deterministic formatting for generated config files

The project driver should not manually parse strings after this lands. It
should read TOML into a structured value tree, then decode that tree into
`ProjectManifest`, `SolutionManifest`, and user config records with ordinary
typed validation.

## 4. Value Model

The public value model should cover TOML data types:

- string
- integer
- float
- boolean
- offset date-time
- local date-time
- local date
- local time
- array
- table
- inline table
- array of tables

The value tree should preserve enough source information for diagnostics and
fixture editing:

- source file name or source identity
- span for each value
- span for keys and table headers
- original key path

Comments and original formatting do not need to be preserved in the first
self-hosting implementation unless fixture-editing tests require it. Formatting
preservation can be a later `System.Toml.Edit` layer if needed.

## 5. Manifest Decoding

`System.Toml` parses TOML. The project driver owns manifest schema validation.

That separation keeps the reusable parser generic while still letting Stark
manifests produce domain-specific diagnostics:

- missing `[project]`
- invalid `project.kind`
- missing target root
- bad dependency inline table
- bad native metadata shape
- unknown profile optimization level
- invalid solution defaults or aliases

The manifest decoder should reject unknown fields only where the manifest schema
decides to be closed. `System.Toml` itself should not know Stark manifest rules.

## 6. Performance Rules

`System.Toml` is on the build hot path, so it should be efficient:

- parse in one pass over input where practical
- avoid per-character heap allocation
- store source spans as ranges into the original text
- intern or share repeated key strings where that fits the compiler interner
  model
- expose typed lookup APIs so callers avoid repeated path splitting
- keep deterministic emission allocation-conscious

The implementation should still prefer correctness and useful diagnostics over
fragile shortcuts. Manifest files are small, but solution/project discovery runs
often enough that the parser should not be casually wasteful.

## 7. Work Items

- [x] Decide TOML strategy: add general reusable `System.Toml`.
- [~] Define and implement the `System.Toml` public model: namespace/module
      layout, value/table/array/key/datetime/diagnostic types, source-span
      representation, and typed lookup/projection helpers for manifest decoding.
      Landed: flat-node `TomlDocument` (kinds Table/Array/Text/Integer/True/
      False) with line/column spans on every node, span-carrying `TomlError`
      variants, `TomlStatus`/`TomlResult<T>`, and Json-parity lookup helpers
      (`TryFindMember`, `TryChildAt`, `TryFindMemberOfKind`, `TextAt`/`KeyAt`/
      `I64At`/`BoolAt`/`LineAt`/`ColumnAt`). Datetime types and richer typed
      projection helpers remain.
- [~] Implement the TOML reader for the chosen standard baseline: lexer,
      parser, duplicate-key/table validation, dotted keys, inline tables,
      arrays, arrays of tables, strings/multiline strings, numeric/boolean
      values, date/time/datetime values, and useful malformed-input diagnostics.
      Landed (staged manifest subset, tracked here as temporary): bare/basic/
      literal keys and strings with \b \t \n \f \r \" \\ \uXXXX escapes,
      dotted keys, table headers, inline tables, arrays, decimal integers with
      underscore validation, booleans, comments, duplicate-key rejection, and
      span-carrying diagnostics; 17 facts in `tests-stark/stdlib.Toml` include
      decoding the repo's real manifest shape. Remaining: multiline strings,
      arrays of tables, floats, date/times, hex/octal/binary integers, and \U
      escapes (currently explicit UnsupportedValue/InvalidEscape diagnostics).
- [x] Implement deterministic TOML writing and file helpers that compose
      `System.IO.File` with `System.Toml` without hiding IO failures.
      Landed: `TomlWriter` sink plus `Write`/`Emit` produce canonical TOML with
      stable key ordering (members of each table sorted by decoded key bytes,
      scalars before sub-table headers, dotted-path headers, arrays in source
      order, inline tables sorted) so the same document is byte-identical and
      parse->emit->parse is stable. `Emit` yields an owned `OwnedAscii`. File
      helpers `ReadFile`/`WriteFile`/`WriteFileAtomic` compose
      `System.IO.File.ReadAllTextInto`/`WriteAllText`/`WriteAllTextAtomic` and
      surface BOTH parse/emit and IO failures explicitly through
      `TomlFileError` (`Io from System.IO.IOError`, `Toml from TomlError`) and
      `TomlFileResult<T>`/`TomlFileStatus`. Value kinds the parser cannot yet
      produce route through `UnsupportedValue` rather than inventing a spelling.
      9 new facts in `tests-stark/stdlib.Toml` cover canonical shape,
      determinism across key order, round-trip idempotence, escapes, arrays,
      header-form top-level inline tables, inline-tables-inside-arrays, and the
      `System.Testing.TempDirectory`-backed read/write helpers including the
      missing-file IO-error path.
- [ ] Replace host-style `SimpleToml` manifest handling in the self-hosted
      project driver with `System.Toml` plus typed decoding for `Stark.toml`,
      `Stark.solution.toml`, and `Stark.user.toml`.
- [~] Add TOML conformance, emitter, malformed-input, source-span, manifest
      decoder, and project-driver error-location tests.
      Landed: conformance, malformed-input, source-span, and real-manifest
      decoding facts (17) plus emitter determinism/round-trip and file-helper
      facts (9) in `tests-stark/stdlib.Toml`. Remaining: manifest decoder and
      project-driver error-location tests (follow the `SimpleToml` replacement
      work item).

## 8. Documentation Work

- [x] Document `System.Toml` in the standard library reference.
- [x] Update project/build docs to say Stark manifests are parsed through
      `System.Toml`.
- [ ] Document the supported TOML version and any temporary bootstrap subset.
