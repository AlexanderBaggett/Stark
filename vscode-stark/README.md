# Stark Language VS Code Extension

This extension provides editor support for Stark source files.

The extension icon is generated from the repository's `brand.svg`, bundled as
`assets/brand.svg` with a package-compatible `assets/brand.png`.

## Features

- `.stark` language registration
- TextMate syntax highlighting for Stark grammar tokens, including:
  - modules and imports
  - attributes
  - functions, `finite`, `law`, and `finite law`
  - structs, records, enums, traits, doctrines, and aliases
  - ownership and borrow qualifiers
  - storage classes
  - integer range types and floating point types
  - wrapping and saturating arithmetic operators
  - switch patterns, loop behavior keywords, and layout queries
  - unsafe, FFI, and assembly function syntax
- Language configuration for comments, brackets, indentation, and auto-closing pairs
- Allman-style snippets for common Stark declarations and statements
- Standard library completions generated from `stdlib/src/System`
  - module import completions
  - qualified completions like `System.Console.`
  - public top-level functions, types, enum cases, constructors, and public member functions
  - snippet placeholders for function parameters

## Development

Open this folder in VS Code and press `F5` to run an Extension Development Host.

Refresh standard library completions after changing `stdlib/src/System`:

```bash
npm run generate:stdlib
```

Run the extension consistency check:

```bash
npm run check
```

The extension intentionally has no npm runtime dependencies. VS Code provides the `vscode` API at extension runtime.

## Packaging

Install `vsce` if you want to package a `.vsix` locally:

```bash
npm install -g @vscode/vsce
vsce package
```

The generated package can be installed through VS Code's "Install from VSIX..." command.
