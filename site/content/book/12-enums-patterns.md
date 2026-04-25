+++
title = "12. Enums and Pattern Matching"
weight = 120
book_part = "Part II: Stark's Core Language"
book_status = "draft"
prev = "/book/11-aggregates-layout/"
next = "/book/13-arrays-slices-text/"
+++

# Enums and Pattern Matching

Enums describe a value that can be one of a closed set of variants. Pattern
matching is how code reads that shape back out.

{{< stark-sample "assets/book/samples/enums-patterns.stark" >}}

## Variants

An enum variant can be unit-like, tuple-like, or named-field:

```stark
enum Token {
    End,
    Integer(i32[min max]),
    Move { X: i32[min max], Y: i32[min max] },
}
```

Construct variants through the enum name:

```stark
Token.End
Token.Integer(5)
Token.Move { X: 2, Y: 3 }
```

The enum is closed. Code that handles `Token` only needs to consider variants
declared on `Token`.

## Switch Patterns

Use `switch` to branch on enum shape:

```stark
switch (token) {
    case Token.End:
        return 0;
    case Token.Integer(var value):
        return value;
    case Token.Move { X: var x, Y: var y }:
        return x + y;
}
```

`var` captures the payload part of the variant. Named-field variants match by
field name, which keeps the match readable when a variant has more than one
payload field.

## Literal, Discard, And Guard Patterns

Patterns are not limited to enums. Stark switch cases also support literal
patterns, discard patterns, captures, and guards:

```stark
switch (value) {
    case 0:
        return false;
    case _:
        return true;
}
```

Use `_` when the value matters only because it matched the surrounding shape.
Use `when` guards when the shape match needs one extra boolean condition.

This checked sample combines a captured value, a literal arm, a guarded
discard, and a default arm:

{{< stark-sample "assets/book/samples/switch-guards.stark" >}}

Switch coverage is checked. Once earlier arms already cover every possible
shape, a later arm is rejected instead of being accepted as dead code:

{{< stark-sample "assets/book/negative-samples/unreachable-switch-default.stark" >}}

## Ownership In Matches

Matching follows Stark ownership rules. Capturing a move-only payload with
`var` can move that payload out of the matched value. Matching scalar payloads,
discarding payloads, or checking a unit-like variant does not create hidden
ownership magic.

The practical rule is simple: after a match extracts owned data, continue to
treat the original value as subject to ordinary move and reinitialization
rules.

## ABI Boundaries

Stark enums are ordinary Stark values, not automatic C ABI contracts. Do not
export enum-shaped data across `ffi` or `export` boundaries unless the boundary
uses an explicitly designed representation.

This rejected example tries to make an ordinary Stark enum part of an exported
ABI:

{{< stark-sample "assets/book/negative-samples/enum-abi-boundary.stark" >}}
