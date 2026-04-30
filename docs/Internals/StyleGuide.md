# Stark Style Guide

This document defines the recommended source-style conventions for Stark code.

The goal is not to imitate one existing language completely. Stark should use:

- C#-style naming for Stark-owned source code
- Rust-style discipline about not encoding visibility into names
- C-style spelling only at explicit ABI and FFI boundaries

This gives Stark a clear identity that matches its current design:

- syntax visually close to C#
- semantics stricter than Rust in several areas
- explicit low-level boundaries inspired by C

## Core Principles

- prioritize clarity over cleverness
- keep Stark-owned APIs visually distinct from foreign APIs
- do not encode visibility or storage class into names
- preserve foreign spellings at the FFI boundary
- use one convention consistently rather than mixing styles within a module

## Naming

### Modules

Use `PascalCase` names for modules.

Examples:

```stark
module System
module System.IO
module System.IO.File
module Math
module DataModel
```

Module names should be nouns or noun phrases, not verbs.

### Types

Use `PascalCase` for:

- `struct` names
- `record` names
- `trait` names
- `doctrine` names

Examples:

```stark
struct Box
record Point
trait Comparable
doctrine NumericLaws
```

Do not prefix trait names with `I`.

Prefer capability or concept names such as `Comparable`, `Hashable`, or `NumericLaws`.

### Functions and Methods

Use `PascalCase` for Stark-native functions and methods.

Examples:

```stark
fn i32 Add(i32 left, i32 right)
public fn void WriteLine(ascii text)
internal finite law i32 HashBytes(borrow i8[] data)
```

This rule applies equally to:

- module-private functions
- `internal` functions
- `public` functions
- `export` functions that are still Stark-owned APIs

Visibility should not change naming style.

Bad idea:

- `public fn i32 add(...)`
- `fn i32 add(...)`

Good idea:

- `public fn i32 Add(...)`
- `fn i32 Add(...)`

### Parameters and Local Variables

Use `camelCase` for:

- function parameters
- local variables
- local constant bindings

Examples:

```stark
fn i32 Add(i32 left, i32 right) 
{
    stack i32 total = left + right;
    return total;
}
```

Prefer short, concrete names. Avoid unnecessary abbreviations unless they are standard and obvious.

### Fields and Record Members

Use `PascalCase` for fields and record members.

Examples:

```stark
struct Box 
{
    i32[min max] Width;
    i32[min max] Height;
}

record Point(i32[min max] X, i32[min max] Y) { }
```

This matches Stark's C#-like surface and keeps type shapes visually consistent with constructors and object initializers.

### Globals and Constants

For Stark-owned top-level globals and constants, use `PascalCase`.

Examples:

```stark
const PageSize = 2**12;
static mut i32[min max] GlobalCounter = 0;
```

Do not use screaming snake case for ordinary Stark declarations.

## Visibility Does Not Affect Naming

A declaration should not be renamed just because it is:

- module-private
- `internal`
- `public`
- `export`

These are semantic distinctions, not style distinctions.

Do not use naming tricks such as:

- leading underscores to mean private
- `m_`, `s_`, `g_`, or similar prefixes
- different casing for public versus non-public declarations

Bad:

```stark
fn i32 _computeHash(i32 value)
internal fn i32 computeHash(i32 value)
public fn i32 ComputeHash(i32 value)
```

Good:

```stark
fn i32 ComputeHash(i32 value)
internal fn i32 ComputeHash(i32 value)
public fn i32 ComputeHash(i32 value)
```

## FFI and ABI Boundary Rules

### Imported FFI Names

Preserve the foreign symbol spelling exactly when declaring imported FFI functions or globals.

Examples:

```stark
ffi fn i32 fputs(ascii text, rawptr<i8> stream);
ffi fn rawptr<i8> fopen(ascii path, ascii mode);
const rawptr<i8> stdout = null;
const rawptr<i8> stderr = null;
```

Do not rename imported foreign symbols into Stark-style `PascalCase` names.

A separate Stark wrapper is justified only when it creates a real semantic or package boundary, not when it exists only to restyle the foreign name.

Bad:

```stark
ffi fn i32 FPuts(ascii text, rawptr<i8> stream);
```

Good:

```stark
ffi fn i32 fputs(ascii text, rawptr<i8> stream);
```

### Exported FFI Names

If a function exists to satisfy a foreign ABI contract, spell it the way the foreign environment expects.

Examples:

```stark
export ffi fn i32 main() 
{
    return 0;
}
```

This does not mean every `ffi` function should be lowercase.

The rule is:

- preserve the ABI-facing spelling expected by foreign consumers

If the foreign API expects `main`, use `main`.

If the foreign API expects `CreateFileW`, use `CreateFileW`.

If the exported symbol is really part of a Stark-facing package API rather than a foreign ABI convention, `PascalCase` is still preferred.

### Cosmetic Wrappers Around FFI

Do not introduce wrappers around FFI imports just to make the foreign names look more Stark-like.

That kind of wrapper usually adds no semantic value and can lengthen hot call paths for style reasons alone.

Bad:

```stark
ffi fn i32 fputs(ascii text, rawptr<i8> stream);

public fn i32 FPuts(ascii text, rawptr<i8> stream) 
{
    return fputs(text, stream);
}
```

If the foreign symbol is `fputs`, call it `fputs`.

### Semantic Wrappers Around FFI

An FFI wrapper is acceptable only when it creates a real Stark boundary.

Typical valid reasons include:

- hiding raw handles or raw pointers from the rest of the package
- combining multiple foreign calls into one Stark-level operation
- exposing a stable package API instead of leaking runtime details directly
- narrowing a low-level foreign surface into a smaller, more intentional Stark abstraction

Example:

```stark
ffi fn i32 fputs(ascii text, rawptr<i8> stream);
const rawptr<i8> stdout = null;

public fn void WriteLine(ascii text) 
{
    fputs(text, stdout);
    fputs("\n", stdout);
    return;
}
```

This is acceptable because it is not just a spelling wrapper. It hides the foreign stream handle and defines a higher-level Stark operation.

If a wrapper adds no semantic value and exists only for naming, prefer direct FFI use instead.

## Entrypoint Rule

For the hosted program entrypoint, use:

```stark
export ffi fn i32 main()
```

Use `main`, not `Main`.

Reason:

- this is an ABI/runtime convention, not an ordinary Stark API name
- the host toolchain expects the conventional entrypoint spelling
- Stark should be explicit at foreign boundaries instead of pretending they are ordinary source-level calls

## Formatting

### Indentation

Use 4 spaces for indentation.

Do not use tabs in Stark source.

### Braces

Use K&R-style braces as in the existing examples:

```stark
fn i32 Add(i32 left, i32 right) 
{
    return left + right;
}
```

### Spacing

- put one space before `{`
- put one space after commas
- do not add extra interior spaces just to line things up vertically
- keep unary and postfix expressions tight

### Imports and Module Declaration

Follow the language rule directly:

1. imports first
2. module declaration second
3. blank line
4. top-level declarations

Example:

```stark
import System
import Math
module App

export ffi fn i32 main() 
{
    return Math.Add(1, 2);
}
```

## Practical Examples

### Stark-Owned Code

```stark
module Geometry

struct Box 
{
    i32 Width;
    i32 Height;
}

public finite law i32 Area(Box box) 
{
    return box.Width * box.Height;
}
```

### FFI Boundary With Wrapper

```stark
module System.Console

ffi fn i32 fputs(ascii text, rawptr<i8> stream);
const rawptr<i8> stdout = null;

public fn void Write(ascii text) 
{
    fputs(text, stdout);
    return;
}
```

### Hosted Entrypoint

```stark
import System
module Hello

export ffi fn i32 main() 
{
    System.Console.WriteLine("Hello, world!");
    return 0;
}
```

## Summary

Use these defaults unless there is a strong reason not to:

- modules: `PascalCase`
- types: `PascalCase`
- Stark-native functions and methods: `PascalCase`
- fields: `PascalCase`
- parameters and locals: `camelCase`
- Stark-owned globals and constants: `PascalCase`
- imported FFI names: preserve foreign spelling exactly
- ABI entrypoint: `main`

In short:

- Stark-owned code should look like Stark
- foreign code should look foreign
- visibility should never be encoded into naming
