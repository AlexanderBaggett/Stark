# Stark Language

VS Code language support for Stark.

This extension provides:

- TextMate syntax highlighting for `.stark` files.
- Stark language configuration for comments, brackets, pairs, folding markers, and indentation.
- Snippets for common declarations and language contracts.
- A first-pass static standard library completion provider generated from `stdlib/src`.

## Development

Open this folder in VS Code and press `F5` to launch an Extension Development Host.

From the repo root, regenerate the standard library completion index with:

```powershell
cd tools/vscode-stark
npm run generate:stdlib
```

Run the lightweight validation checks with:

```powershell
npm run check
```

## Completion Scope

The completion provider is intentionally static. It knows about standard library modules and public symbols, including public type members found in the standard library source. It does not perform type checking, local scope analysis, overload resolution, or import graph visibility filtering. Those belong in the later compiler-backed language server.
