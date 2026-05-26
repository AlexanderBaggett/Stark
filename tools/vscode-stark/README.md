# Stark Language

VS Code language support for Stark.

Programming language repository: [github.com/AlexanderBaggett/Stark](https://github.com/AlexanderBaggett/Stark)

This extension provides:

- TextMate syntax highlighting for `.stark` files.
- Stark language configuration for comments, brackets, pairs, folding markers, and indentation.
- Snippets for common declarations and language contracts.
- A first-pass static standard library completion provider generated from `stdlib/src`.

## Completion Scope

The completion provider is intentionally static. It knows about standard library modules and public symbols, including public type members found in the standard library source. It does not perform type checking, local scope analysis, overload resolution, or import graph visibility filtering. Those belong in the later compiler-backed language server.
