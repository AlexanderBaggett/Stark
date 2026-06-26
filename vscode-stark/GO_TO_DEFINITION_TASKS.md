# Go To Definition Task List

Short-lived tracker for adding a pragmatic Go to Definition implementation to the Stark VS Code extension. Delete this file once the feature is implemented and covered.

- [x] Choose a lightweight direct VS Code `DefinitionProvider` approach instead of a language server.
- [x] Define MVP behavior for exact, ambiguous, and unresolved definition results.
- [x] Add a workspace `.stark` file discovery helper.
- [x] Build a lightweight declaration index for modules, imports, functions, laws, types, aliases, enum members, methods, constructors, and fields.
- [x] Track source file URI, declaration name, qualified name, kind, line, column, and range for indexed declarations.
- [x] Include open unsaved Stark documents in the index.
- [x] Refresh the index on activation, Stark file open/save/create/delete, and relevant workspace folder changes.
- [x] Add cursor target extraction for plain identifiers, dotted qualified names, qualified function calls, generic enum cases, and import paths.
- [x] Resolve import/module targets such as `import System.Console` and `module Benchmarks.Console.ConsoleReadSurface`.
- [x] Resolve qualified type and module references such as `System.Memory.MemoryResult`.
- [x] Resolve qualified function calls such as `System.Console.ReadLine()`.
- [x] Resolve enum case references such as `System.IO.IOStatus.Ok` and `System.IO.IOResult<System.IO.File.File>.Err(...)`.
- [x] Resolve unqualified identifiers using current document imports before falling back to workspace-wide candidates.
- [x] Return multiple `vscode.Location` results when a name is ambiguous.
- [x] Register the provider with `vscode.languages.registerDefinitionProvider({ language: "stark" }, provider)`.
- [x] Add focused resolver tests that can run under Node outside VS Code.
- [x] Add manual smoke-test notes for Go to Definition in stdlib, benchmarks, tests, and selfhost files.
- [x] Run `npm run check` for `vscode-stark`.

## Follow-Up Hardening

- [x] Avoid indexing method-local declarations as type fields.
- [x] Use the full module path as the module declaration location range.
- [x] Preserve the original source line in import/module target extraction ranges.
- [x] Add a fallback for instance-style member references such as `container.Value` when no exact qualified match exists.
- [x] Add resolver tests for unresolved names returning no locations.
- [x] Add resolver tests that local variables inside methods are not indexed as fields.
- [x] Add resolver tests for direct field lookup through the instance-member fallback.
- [x] Re-run `npm run check` for `vscode-stark`.

## Local Symbol Follow-Up

- [x] Track function/method scopes in the document index.
- [x] Index same-line function and method parameters as document-local definitions.
- [x] Index simple local bindings declared with `stack`, `heap`, `arena`, `register`, `const`, and `var`.
- [x] Resolve unqualified local identifiers before imported and workspace-wide symbols.
- [x] Resolve instance-style base identifiers such as `container` in `container.Value` to local declarations when possible.
- [x] Keep local declarations out of the workspace/global symbol index.
- [x] Add resolver tests for parameter Go to Definition.
- [x] Add resolver tests for local variable Go to Definition.
- [x] Add resolver tests for local base-name Go to Definition in member access.
- [x] Re-run `npm run check` for `vscode-stark`.

## Multi-Line Signature Follow-Up

- [x] Index parameters declared across multi-line function and method signatures.
- [x] Resolve multi-line parameters before imported and workspace-wide symbols.
- [x] Keep multi-line parameter declaration ranges on the parameter name token.
- [x] Add resolver tests for multi-line parameter Go to Definition.
- [x] Add resolver tests for multi-line parameter base-name Go to Definition in member access.
- [x] Re-run `npm run check` for `vscode-stark`.

## Pattern Binding Follow-Up

- [x] Index `case ... var name` pattern bindings as document-local definitions.
- [x] Stop pattern binding scans at the top-level case-arm colon so record-field colons do not truncate the pattern.
- [x] Resolve pattern bindings before imported and workspace-wide symbols.
- [x] Add resolver tests for enum case pattern bindings.
- [x] Add resolver tests for bare `case var name when ...` guard bindings.
- [x] Keep pattern bindings out of the workspace/global symbol index.
- [x] Re-run `npm run check` for `vscode-stark`.

## MVP Behavior

- Exact matches return the matching declaration location.
- Ambiguous unqualified names return every matching declaration location so VS Code can present choices.
- Unresolved names return no locations.
- Resolution is heuristic and source-index based; it does not type-check, bind overloads, or prove local scope correctness.
- Local parameters and simple local bindings are resolved before workspace symbols.

## Manual Smoke-Test Notes

- Stdlib: Go to Definition on `System.Console.ReadLine()` should open the `ReadLine` declaration in `stdlib/src/System/Console.stark`.
- Benchmarks: Go to Definition on imported modules and fully qualified stdlib calls should jump to matching stdlib files.
- Tests: Go to Definition on `System.IO.IOStatus.Ok` should jump to the enum member declaration.
- Selfhost: Go to Definition on module-qualified compiler types/functions should prefer exact qualified matches and show multiple choices for ambiguous unqualified names.
