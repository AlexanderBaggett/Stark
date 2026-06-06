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
- [ ] Create `System.Toml` namespace/module layout.
- [ ] Define TOML value, table, array, key, datetime, and diagnostic types.
- [ ] Implement lexer/tokenizer with source span tracking.
- [ ] Implement parser for the chosen TOML standard baseline.
- [ ] Implement duplicate-key/table redefinition validation.
- [ ] Implement dotted keys, inline tables, arrays, and arrays of tables.
- [ ] Implement string escape and multiline string rules.
- [ ] Implement integer, float, boolean, date, time, and datetime parsing.
- [ ] Implement deterministic TOML emitter/writer.
- [ ] Add file helpers that compose `System.IO.File` with `System.Toml` without
      hiding IO failures.
- [ ] Add typed lookup/projection helpers for required/optional strings,
      integers, arrays, tables, inline tables, and enums.
- [ ] Replace host-style `SimpleToml` logic in the self-hosted project driver
      with `System.Toml` plus typed manifest decoding.
- [ ] Add manifest decoder tests for `Stark.toml`, `Stark.solution.toml`, and
      `Stark.user.toml`.
- [ ] Add TOML parser/emitter conformance tests, malformed-input diagnostics,
      source-span tests, and deterministic-emission tests.
- [ ] Add project-driver tests that prove manifest errors point at the right
      TOML source spans.

## 8. Documentation Work

- [ ] Document `System.Toml` in the standard library reference.
- [ ] Update project/build docs to say Stark manifests are parsed through
      `System.Toml`.
- [ ] Document the supported TOML version and any temporary bootstrap subset.
