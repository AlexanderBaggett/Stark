# Stark Language

Editor support for Stark source files.

Programming language repository: [github.com/AlexanderBaggett/Stark](https://github.com/AlexanderBaggett/Stark)

## Features

- Syntax highlighting for `.stark` files, including:
  - modules and imports
  - attributes
  - functions, `finite`, `law`, `finite law`, `tail`, and `become`
  - structs, records, enums, traits, doctrines, and aliases
  - ownership and borrow qualifiers
  - storage classes, including arena storage
  - memory contracts, including `disjoint`, `overlap`, `same`, and `dead_on_return`
  - arena-backed dynamic storage allocation with `new(arena, ...)`
  - integer range types and floating point types
  - wrapping and saturating arithmetic operators
  - switch patterns, loop behavior keywords, and layout queries
  - unsafe, FFI, `[LinkName("...")]`, C aggregate interop, and assembly function syntax
- Comment toggling, bracket matching, indentation, and auto-closing pairs
- Snippets for common Stark declarations and statements
  - Vendor import snippets for the bundled supported bindings
- Standard library autocomplete
  - module imports
  - qualified completions like `System.Console.`
  - functions, types, enum cases, constructors, fields, and member functions
  - function-call snippets with parameter placeholders
