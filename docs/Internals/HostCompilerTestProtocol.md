# Host Compiler Test Protocol

The host compiler exposes a structured test protocol for Stark-native tests
that need to compile snippets against the current C# compiler. The protocol is
intended for self-host prep tests that need compiler diagnostics, pass
execution data, and rendered artifacts without scraping human CLI output.

## Commands

```bash
compiler --host-test-inspect [request.json]
compiler --host-test-server
```

`--host-test-inspect` reads one JSON document from a file or stdin and writes
one indented JSON response. `--host-test-server` reads one compact JSON document
per stdin line and writes one compact JSON response per line. Send
`{"protocolVersion":1,"shutdown":true}` to end a server session cleanly.

Use the server form for many small compiler tests. It keeps the host process and
default compiler pipeline warm while rebuilding module resolution per request,
so fixture edits and generated package images are visible without restarting the
whole process.

Stark-native tests should drive server batches with
`System.Process.RunCaptureWithInputTimeout`: write one compact JSON document per
line followed by the shutdown document, then parse or snapshot the captured
stdout lines and assert the process did not time out.

## Determinism Rules

- Set `protocolVersion` to `1`.
- Valid protocol documents return process exit code `0`; compile failures are
  represented inside the JSON response.
- Invalid JSON, unreadable request files, or invalid command-line usage return a
  non-zero process exit code.
- `sourceText` takes precedence over `sourcePath`; an empty `sourceText` is a
  real source input, not a missing field.
- The source file directory is searched first when `filePath` or `sourcePath`
  is available. Request `searchDirectories` are searched next.
- `STARK_PATH` is ignored unless `useStarkPath` is explicitly `true`.
- Request only the artifacts and logs a test needs. Artifact rendering can be
  more expensive than compiling short parser/type-check snippets.
- Use `includeArtifactTexts: false` with `artifactOutputDirectory` for large
  MIR/SSA/LLVM golden tests so the JSON response stays small.

## Request Shape

```json
{
  "protocolVersion": 1,
  "request": {
    "id": "simple-llvm",
    "sourceText": "module Demo\n\nfn i32[min max] Run()\n{\n    return 7;\n}\n",
    "filePath": "Demo.stark",
    "stopAfterPassId": "emit-llvm",
    "optimizationLevel": "O0",
    "artifacts": ["llvm", "mir", "optimized-ssa"],
    "includeArtifactTexts": false,
    "artifactOutputDirectory": "build/dev/x86_64/stage0/artifacts/Demo",
    "diagnosticsOutputPath": "build/dev/x86_64/stage0/diagnostics/Demo.json",
    "includeLogs": true,
    "includeExecutions": true
  }
}
```

Batch documents use `requests` instead of `request`:

```json
{
  "protocolVersion": 1,
  "requests": [
    { "id": "parse", "sourceText": "module A", "stopAfterPassId": "syntax-model" },
    { "id": "type", "sourcePath": "B.stark", "stopAfterPassId": "type-check" }
  ]
}
```

Supported compile options include:

- `sourceText`, `sourcePath`, `filePath`
- `searchDirectories`, `useStarkPath`
- `stopAfterPassId`, `emitLlvmIr`, `continueAfterErrors`
- `strictIntegerRanges`, `optimizationLevel`
- `targetTriple`, `targetDataLayout`, `targetCpu`, `targetFeatures`
- `relocationModel`, `codeModel`
- `qualifyModuleSymbols`, `internalizeModulePrivate`
- `importedInlineCloneSeedFunctions`
- `maximumCompileTimeLoopIterations`
- `artifacts`, `includeArtifactTexts`, `artifactOutputDirectory`
- `diagnosticsOutputPath`, `logsOutputPath`, `executionsOutputPath`
- `includeLogs`, `includeExecutions`

## Artifact Requests

Artifact names can be exact compiler artifact keys or one of these aliases:

| Alias | Artifact |
|---|---|
| `llvm`, `llvm-text`, `llvm-ir` | `llvm-ir-module` |
| `mir`, `mir-text` | `mid-level-ir` |
| `ssa`, `ssa-text` | `ssa-ir` |
| `optimized-ssa`, `optimized-ssa-text`, `opt-ssa`, `opt-ssa-text` | `optimized-ssa-ir` |

`availableArtifacts` lists every artifact key produced by the pipeline.
`artifactTexts` contains rendered text for requested artifacts that have a text
renderer unless `includeArtifactTexts` is `false`. `artifactFiles` lists files
written under `artifactOutputDirectory`. `missingArtifacts` contains requested
artifacts that were not produced. `unsupportedArtifacts` contains produced
artifacts that currently have no text renderer.

Artifact file names are stable:

| Artifact | File |
|---|---|
| `llvm-ir-module` | `llvm-ir-module.ll` |
| `mid-level-ir` | `mid-level-ir.mir` |
| `ssa-ir` | `ssa-ir.ssa` |
| `optimized-ssa-ir` | `optimized-ssa-ir.ssa` |

Other renderable artifact keys use a sanitized `<artifact>.txt` name.

## Response Shape

Document responses contain:

- `protocolVersion`
- `shutdown`
- `protocolError`
- `responses`

Each compile response contains:

- `id`
- `succeeded`
- `protocolError`
- `durationMicroseconds`
- `diagnosticSummary`
- `diagnostics`
- `logs`
- `executions`
- `availableArtifacts`
- `artifactTexts`
- `artifactFiles`
- `missingArtifacts`
- `unsupportedArtifacts`
- `diagnosticsOutputPath`
- `logsOutputPath`
- `executionsOutputPath`
- `outputErrors`
- `rootModuleName`
- `loadedModules`

Diagnostics include `code`, `severity`, `message`, `stage`, and optional source
location fields. Stark tests can assert adapted diagnostic data with
`System.Testing.Diagnostic` and the finite-law `Diagnostic*` / `Diagnostics*`
predicates; JSON parsing/adaptation remains a separate stdlib/test harness
piece. Logs include stable compiler log metadata and sorted `data`.
Executions include pass id, phase, status, duration, and diagnostics added.
Requested output files are indented JSON except artifact text files, which
contain the renderer output directly. Output write failures are reported in
`outputErrors` without changing the compiler result.
