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
| `llvm`, `llvm-text`, `llvm-ir` | `llvm-ir-module` (raw renderer text) |
| `llvm-normalized`, `llvm-ir-normalized`, `llvm-norm` | `llvm-ir-module` normalized like the C# `GetLlvm` oracle: `\bnoundef\b` stripped, then whitespace collapsed on `define`/`declare` header lines |
| `mir`, `mir-text` | `mid-level-ir` |
| `ssa`, `ssa-text` | `ssa-ir` |
| `optimized-ssa`, `optimized-ssa-text`, `opt-ssa`, `opt-ssa-text` | `optimized-ssa-ir` |

**Option-flag suffixes on the artifact name.** A requested LLVM artifact name may
carry `;`-delimited option flags that toggle `CompilerOptions` for that compile
without adding request fields, e.g. `"llvm-normalized;qualify;internalize"`. The
base name before the first `;` selects the rendered artifact; recognized flags:
`qualify` → `QualifyModuleSymbols`, `internalize` → `InternalizeModulePrivate`.
The full requested string is echoed back as the artifact's `requestedName` (so the
caller reads it back by the same name). This is how the Stark
`CompileLlvmNormalizedQualified` / `CompileLlvmInternalized` harness entry points
express the symbol-qualification / executable-internalization tests.

`targetDataLayout` is a dedicated request field (read by `BuildTargetInfo` next
to `targetTriple`); the Stark `CompileLlvmForTargetWithDataLayout` harness entry
sets it so a ported test can assert the emitted `target datalayout = "…"` module
header. `importedInlineCloneSeedFunctions` is a JSON array of function names that
seeds `CompilerOptions.ImportedInlineCloneSeedFunctions`: the LLVM emitter keeps
strong owned definitions only for functions reachable from the seeds and falls
non-reachable bodies back to weak `weak_odr` definitions (the
`BuildOwnedFunctionDefinitionFilter` dependency-pruning path). The Stark
`CompileLlvmWithSeeds(id, source, seed)` harness entry emits this array, so a
ported LTO/dependency-pruning oracle can assert that a seed narrows the owned
emission surface.

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

**SSA renderer fidelity — `ssa` vs `optimized-ssa`.** The two SSA artifacts render at
different detail levels, which matters when picking the one an assertion reads:

- `ssa` (`lowering.ssa`) is the **terse, pre-optimization** lowering SSA. It prints each
  instruction as `vN = <op> @ line:col` — operator and source location only, **without
  operands or constant coefficients** (e.g. a repeated-add run prints `v0 = + @ 5:5`,
  `v1 = + @ 5:5`).
- `optimized-ssa` (`lowering.ssa.optimized`) is where the **optimization passes write**,
  and it prints fold-*synthesized* instructions with their full operands and coefficients
  (`v1_mul_1 = arg_value * 3 @ 5:5`, `+ 11`, `arg_value ** 3`, `return 0`). Instructions that
  a pass leaves untouched still print terse (`vN = +`), so a *preserved* operand (e.g. a
  trailing `+ 5`) may not be literally renderable.

So an assertion about an operand/coefficient/fold result, or about a pass having removed a
load/store, must read `optimized-ssa` — and the bridge produces `optimized-ssa` even for an
**early** `stopAfterPassId` (e.g. stopping after `arithmetic-fold-ssa`, `memory-opt-ssa`,
`cleanup-ssa`, or `inline-ssa` still fills the optimized artifact with the state after that
pass). The Stark harness exposes this as `SsaTestSupport.CompileSsaAfterOptimized(id, source,
stopAfterPassId)` (requests `optimized-ssa` at the stop) alongside `CompileSsaAfter` (terse
`ssa`). To scope a "no surviving call" check to one function (so a callee's own surviving
`fn <callee>(` header is not matched), slice with `System.Testing.SsaFunctionBody(ssaText,
fnName)` — the SSA analogue of `LlvmDefinitionBody` — via `OptimizedSsaFunctionLacks/Contains`.

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
piece.

Two location guarantees worth asserting against:

- Diagnostics produced while checking an imported source module carry the
  imported module's own file path in their location fields, not the root
  compilation path. (Only the diagnostic funnels re-path; the syntax model's
  `Location()` values keep stamping the root path because package-image
  template records match by location.)
- A duplicated parameter name in a function signature is an ordinary located
  diagnostic (STK3057 naming the parameter and function; the first
  declaration stays authoritative), never an STK9999 pass crash. Logs include stable compiler log metadata and sorted `data`.
Executions include pass id, phase, status, duration, and diagnostics added.
Requested output files are indented JSON except artifact text files, which
contain the renderer output directly. Output write failures are reported in
`outputErrors` without changing the compiler result.
