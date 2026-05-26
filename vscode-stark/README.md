# Stark Language

Editor support for Stark source files.

Programming language repository: [github.com/AlexanderBaggett/Stark](https://github.com/AlexanderBaggett/Stark)

## Features

- Syntax highlighting for `.stark` files, including:
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
- Comment toggling, bracket matching, indentation, and auto-closing pairs
- Snippets for common Stark declarations and statements
- Standard library autocomplete
  - module imports
  - qualified completions like `System.Console.`
  - functions, types, enum cases, constructors, fields, and member functions
  - function-call snippets with parameter placeholders
