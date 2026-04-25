+++
title = "Appendix C: Integer Widths and Range Rules"
weight = 370
book_part = "Appendices"
book_status = "draft"
prev = "/book/appendix-b-operators/"
next = "/book/appendix-d-function-kinds/"
+++

# Appendix C: Integer Widths and Range Rules

Stark integer source types always carry an explicit runtime range.

{{< stark-sample "assets/book/samples/values-ranges.stark" >}}

Read each integer type as two pieces of information: the representation width
and the value range the program intends to permit. `u8[0 127]` is an unsigned
8-bit representation with a narrower source-level range than the whole `u8`
width.

## Width Families

Signed widths:

`i8`, `i16`, `i24`, `i32`, `i48`, `i64`, `i96`, `i128`, `i192`, `i256`,
`i384`, `i512`, `i768`, `i1024`

Unsigned widths:

`u8`, `u16`, `u24`, `u32`, `u48`, `u64`, `u96`, `u128`, `u192`, `u256`,
`u384`, `u512`, `u768`, `u1024`

## Range Syntax

Examples:

```stark
i32[0 100]
i32[min max]
u8[0 255]
u64[min max]
i32[10**2 10**10]
```

For signed widths, `min` and `max` are the signed minimum and maximum for that
width. For unsigned widths, `min` is `0` and `max` is the largest value for the
width.

## Constants

Runtime values spell ranges. Scalar integer constants usually do not:

```stark
const PageSize = 4096;
stack i32[0 max] count = PageSize;
```

Use explicit casts or helper APIs when moving between ranges or
representations.

Implicit narrowing is rejected even when a particular call site passes a small
literal. The function body still has to be valid for every value promised by
its parameter type:

{{< stark-sample "assets/book/negative-samples/implicit-integer-narrowing.stark" >}}

Range spelling should be part of API design. A score, length, index, exit code,
or byte value often has a smaller meaningful domain than its machine width.
