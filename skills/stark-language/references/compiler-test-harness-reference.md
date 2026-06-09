# Stark Compiler Test Harness Reference

Use the host compiler test protocol when Stark tests need to target the current
C# compiler and inspect structured compiler results without scraping CLI text.

## Commands

```bash
compiler --host-test-inspect request.json
compiler --host-test-inspect
compiler --host-test-server
```

`--host-test-inspect` reads a single JSON document from a file or stdin and
writes one indented JSON response. `--host-test-server` reads newline-delimited
JSON documents from stdin and writes one compact JSON response per input line.
End a server session with:

```json
{"protocolVersion":1,"shutdown":true}
```

Prefer the server mode for large ported test batches. It keeps one host process
and compiler pipeline alive, while rebuilding module resolution for each request
so generated fixtures and package images remain visible.

From Stark tests, use `System.Process.RunCaptureWithInputTimeout` to send one
compact JSON document per line plus the shutdown document to
`--host-test-server`, then assert the captured exit code, stdout JSON lines,
stderr text, and non-timeout status.

## Request Example

```json
{
  "protocolVersion": 1,
  "request": {
    "id": "llvm-smoke",
    "sourceText": "module Demo\n\nfn i32[min max] Run()\n{\n    return 7;\n}\n",
    "filePath": "Demo.stark",
    "stopAfterPassId": "emit-llvm",
    "optimizationLevel": "O0",
    "artifacts": ["llvm", "mir", "optimized-ssa"],
    "includeArtifactTexts": false,
    "artifactOutputDirectory": ".stark/build/dev/x86_64/stage0/artifacts/Demo",
    "diagnosticsOutputPath": ".stark/build/dev/x86_64/stage0/diagnostics/Demo.json",
    "includeExecutions": true
  }
}
```

Use `requests` for a batch document. Valid protocol documents return process
exit code `0`; compiler errors are response data.

## Source And Resolution

- Use `sourceText` for inline tests and `sourcePath` for file fixtures.
- `sourceText` takes precedence over `sourcePath`, and an empty string is a real
  source input.
- `filePath` controls diagnostic file paths and seeds import search. If omitted
  with `sourcePath`, the source path is used.
- Search order is source/file directory, request `searchDirectories`, then
  `STARK_PATH` only when `useStarkPath` is `true`.
- Keep `useStarkPath` false for deterministic tests unless the test is
  explicitly covering that escape hatch.

## Artifacts

Common aliases:

| Request | Artifact |
|---|---|
| `llvm`, `llvm-text`, `llvm-ir` | `llvm-ir-module` |
| `mir`, `mir-text` | `mid-level-ir` |
| `ssa`, `ssa-text` | `ssa-ir` |
| `optimized-ssa`, `optimized-ssa-text`, `opt-ssa`, `opt-ssa-text` | `optimized-ssa-ir` |

Responses include `availableArtifacts`, `artifactTexts`, `artifactFiles`,
`missingArtifacts`, and `unsupportedArtifacts`. Request only the artifact text a
test actually asserts; short parser/type-check tests should usually inspect
diagnostics and pass executions only.

For large golden tests, set `includeArtifactTexts` to `false` and pass
`artifactOutputDirectory`. The runner writes stable names:
`llvm-ir-module.ll`, `mid-level-ir.mir`, `ssa-ir.ssa`, and
`optimized-ssa-ir.ssa`. Use `diagnosticsOutputPath`, `logsOutputPath`, and
`executionsOutputPath` for file-backed structured output when Stark tests should
snapshot files instead of carrying large JSON response payloads.

## Result Fields

Each compile response includes `id`, `succeeded`, optional `protocolError`,
`durationMicroseconds`, diagnostic summary/detail, optional logs, pass
executions, root module name, loaded modules, requested artifact text/files,
output paths, and output write errors.

Adapt response diagnostics into `System.Testing.Diagnostic` when asserting from
Stark tests. The finite-law `Diagnostic*` and `Diagnostics*` predicates cover
code, severity, stage, message substring/equality, location, end location, and
severity counts without allocating or scraping rendered text.

Set `includeLogs` only when testing log behavior. `includeExecutions` defaults
to included; set it to `false` for very large batches that only need
diagnostics/artifacts.

## Stark Test Function Kinds

Prefer `finite law` for pure local test predicates and pure `[Fact]` / `[Theory]`
bodies. Generated runners call tests directly through `System.Testing.RunFact`,
so the stronger kind flows into the ordinary optimizer without a wrapper. Keep
helpers that perform fixture IO, process execution, console output, snapshot
writes, or owned result consumption as plain `fn` until their full callees and
ownership effects justify a stronger declaration.

Use `[Theory]` with one or more data rows. `[InlineData(...)]` is best for small
constant cases; it supports strings, booleans, signed integers, and qualified
names, and filters match generated row display names such as `Adds(1, 2, 3)`.

Use typed indexed `[MemberData(provider, rowType, count, ...fields)]` for larger
or computed/shared tables. The provider is called once per selected row with the
zero-based row index and returns `rowType`; `count` is a positive integer
literal. Optional field names map row fields to theory parameters by order. The
generated runner materializes one stack row local and calls the theory directly,
so filters such as `--filter AddRows:1` do not construct unselected rows. Prefer
`finite law` providers when the table is pure.

Use `[Platform(...)]` and `[SkipPlatform(...)]` on facts, theories, structs, or
records when a ported test depends on a target OS, architecture, or exact target
triple. The generated runner resolves gates at build time from
`stark test --target`, calls `System.Testing.SkipFact` for gated-out tests, and
emits no call to the test body.

Use `[Collection(name)]` for tests that share a serialized resource. The name may
be a string literal or qualified identifier. `[Serial]` is shorthand for the
reserved `Serial` collection. Struct/record-level collections apply to contained
test methods, and the generated runner emits tests in stable collection groups
with source order preserved inside each named collection.

For LLVM, MIR, SSA, and diagnostic text ports, use `System.Testing.Contains`,
`DoesNotContain`, `StartsWith`, `EndsWith`, `CountOccurrences`, and
`Occurrences` before writing local scan helpers. These helpers are finite-law and
allocation-free; `CountOccurrences` counts non-overlapping matches and returns
`0` for an empty needle.
For captured process output, use `ProcessStdoutOccurrences` /
`ProcessStderrOccurrences` or their count-returning companions to stay on
borrowed stdout/stderr byte slices.
