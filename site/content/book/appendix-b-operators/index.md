+++
title = "Appendix B: Operators and Symbols"
weight = 380
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-a-keywords/"
next = "/book/appendix-c-integer-widths/"
+++

# Appendix B: Operators and Symbols

This appendix summarizes the most common Stark operators and symbols.

{{< stark-sample "samples/operators-basics.stark" >}}

The sample uses arithmetic operators, compound assignment, comparison and logic
operators, and the conditional expression. The rest of this appendix is a quick
lookup table for the same syntax family.

## Arithmetic

- `+`, `-`, `*`, `/`, `%`
- `**` for exponentiation
- `+%`, `-%`, `*%` for wrapping arithmetic
- `+|`, `-|`, `*|` for saturating arithmetic

`^` is bitwise XOR, not exponentiation.

## Assignment

- `=`
- `+=`, `-=`, `*=`, `/=`, `%=`
- `+%=`, `-%=`, `*%=`
- `+|=`, `-|=`, `*|=`
- `&=`, `|=`, `^=`

## Comparison And Logic

- `==`, `!=`
- `<`, `>`, `<=`, `>=`
- `&&`, `||`, `!`

Comparison operators may be chained.

## Bitwise And Pointer-Oriented Operators

- `&`, `|`, `^`, `~`
- `<<`, `>>`
- unary `&` for address-of
- unary `*` for raw-pointer dereference

Raw-pointer operations belong at explicit low-level boundaries.

## Calls, Access, And Structs/Records

- `()` for calls and parameter lists
- `.` for member access and qualified names
- `[]` for fixed arrays, slices, indexing, and text views
- `{}` for blocks, field initializers, enum named-field constructors, and
  array initializers
- `,` between list items
- `:` in cases, enum named fields, and named patterns
- `;` to terminate declarations and statements where the grammar requires it

## Other Forms

- `?:` conditional expression
- `=>` lambda arrow
- `$"..."` interpolated text literal
- `_` discard pattern
- `(targetType)value` explicit conversion
