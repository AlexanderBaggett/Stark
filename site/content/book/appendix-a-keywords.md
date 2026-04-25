+++
title = "Appendix A: Keywords and Reserved Words"
weight = 350
book_part = "Appendices"
book_status = "draft"
prev = "/book/34-project-performance-case-study/"
next = "/book/appendix-b-operators/"
+++

# Appendix A: Keywords and Reserved Words

This appendix lists the current Stark keyword surface.

{{< stark-sample "assets/book/samples/keywords-tour.stark" >}}

The sample intentionally uses common keywords from several groups: `module`,
`const`, `alias`, `struct`, `finite`, `mut`, `borrow`, `enum`, `switch`,
`case`, `default`, `export`, `ffi`, `stack`, `while`, `willexit`, and `return`.

Not every reserved word should appear in ordinary code. Some words are for
low-level boundaries, future-facing language surface, or specialized proof
contexts. Prefer the smallest vocabulary that honestly describes the program.

## Modules And Visibility

- `import`
- `module`
- `internal`
- `public`
- `export`

## Functions And Modifiers

- `fn`
- `finite`
- `law`
- `inline`
- `noinline`
- `inlinehint`
- `hot`
- `cold`
- `ffi`
- `varargs`
- `unsafe`
- `strictfp`
- `asm`
- `static`

## Data Declarations

- `struct`
- `record`
- `enum`
- `trait`
- `doctrine`
- `alias`
- `drop`
- `const`

## Storage And Types

- `stack`
- `heap`
- `register`
- `arena`
- `borrow`
- `retborrow`
- `storeborrow`
- `frozen`
- `shared`
- `out`
- `init`
- `mut`
- `rawptr`
- `rawmutptr`
- `fnptr`
- `sizeof`
- `alignof`

## Control Flow And Patterns

- `if`
- `else`
- `switch`
- `case`
- `default`
- `when`
- `while`
- `for`
- `infinite`
- `non-deterministic`
- `willexit`
- `return`
- `break`
- `continue`
- `where`
- `var`

## Builtins And Literals

- `void`
- `bool`
- `ascii`
- `unicode`
- `Ascii`
- `Unicode`
- `true`
- `false`
- `null`
- integer width names such as `i32`, `u64`, and `i1024`
- floating-point width names such as `f32` and `f64`

Use these as language words, not ordinary identifiers.
