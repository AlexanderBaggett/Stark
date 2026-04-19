# Integer Range Endpoint Expressions

This document tracks the planned work to make Stark integer ranges less tedious while keeping the syntax explicit, consistent, and easy to parse.

The goal is to allow richer expressions inside integer range bounds such as:

```stark
i32[min max]
i64[0 max]
i32[10**2 10**10]
i32[2**4 2**16]
i32[someInteger**someInteger x**y]
i32[0 length - 1]
i64[0 sizeof(Buffer) - 1]
```

## Accepted Scope
### Compile Time Scope

- [ ] Add type-relative integer bounds.
  - [ ] Support `min` as the minimum value of the containing integer type.
  - [ ] Support `max` as the maximum value of the containing integer type.
  - [ ] Support examples such as `i32[min max]`, `i64[0 max]`, and `u8[min 127]`.
  - [ ] Reject `min` or `max` where there is no containing integer type that gives them meaning.

- [ ] Add constant arithmetic in range endpoints.
  - [ ] Support ordinary integer arithmetic in endpoint expressions.
  - [ ] Support exponentiation with `**`.
  - [ ] Support examples such as `i32[10**2 10**10]`, `i32[2**4 2**16]`, and `i64[1024 * 1024 1024 * 1024 * 1024]`.
  - [ ] Diagnose constant endpoint overflow during compile-time evaluation.
  - [ ] Preserve the current requirement that the lower endpoint must not exceed the upper endpoint when both are compile-time-known.

- [ ] Add named constants in range endpoints.
  - [ ] Allow constants declared with `const` to appear in endpoint expressions.
  - [ ] Support expressions such as `PageSize - 1` after `const i32 PageSize = 4096;`.
  - [ ] Resolve constants using the same module/import visibility rules as other Stark names.
  - [ ] Reject non-constant locals in compile-time-only endpoint contexts.

### Later Scope

- [ ] Add type-member bounds.
  - [ ] Support explicit type-qualified bounds such as `i32[i16.min i16.max]`.
  - [ ] Consider whether C#-style casing should also be accepted, such as `i32[i16.Min i16.Max]`.
  - [ ] Keep the accepted spelling documented and consistent across the grammar, parser, and language reference.
  - [ ] Diagnose type-member bounds that do not refer to integer types.


### Runtime Scope
- [ ] Add runtime-dependent endpoint expressions.
  - [ ] Support endpoint expressions that reference values in scope, such as `fn i32[0 length - 1] LastIndex(i32[1 max] length)`.
  - [ ] Support runtime exponent expressions such as `i32[someInteger**someInteger x**y]`.
  - [ ] Define when runtime endpoint expressions are allowed in signatures, locals, returns, and generic/package-image metadata.
  - [ ] Define what runtime checks are required when a value enters a dependent range.
  - [ ] Define what proof facts can eliminate runtime checks.
  - [ ] Define how dependent endpoint expressions affect type identity and assignability.

- [ ] Add size endpoint expressions.
  - [ ] Support `sizeof(Type)` in integer range endpoints.
  - [ ] Support examples such as `i64[0 sizeof(Buffer) - 1]`.
  - [ ] Ensure these expressions use target-aware layout information.
  - [ ] Ensure package-image-backed imports preserve enough layout facts to evaluate these expressions.

## Compiler Work

- [ ] Update the Stark grammar.
  - [ ] Extend integer range endpoint grammar from literal-only bounds to endpoint expressions.
  - [ ] Add grammar coverage for `min`, `max`, type-member bounds, arithmetic, `**`, `sizeof`,
  - [ ] Keep the range syntax square-bracket based and avoid mixed delimiter forms.

- [ ] Update parsing and syntax modeling.
  - [ ] Preserve endpoint expressions in the syntax model instead of flattening them too early.
  - [ ] Record source spans for precise diagnostics inside range endpoints.
  - [ ] Add parser smoke tests for all accepted endpoint expression forms.

- [ ] Update type checking.
  - [ ] Resolve endpoint names and constants in the correct scope.
  - [ ] Evaluate compile-time endpoints when possible.
  - [ ] Distinguish compile-time-known ranges from runtime-dependent ranges.
  - [ ] Diagnose invalid endpoint expression types.
  - [ ] Diagnose reversed compile-time-known ranges.
  - [ ] Preserve runtime-dependent range facts in the type model.

- [ ] Update semantic validation and proof propagation.
  - [ ] Model runtime range checks for dependent endpoints.
  - [ ] Reuse existing integer range facts where possible.
  - [ ] Feed proven endpoint facts into assignment, call, return, and comparison validation.
  - [ ] Ensure dependent endpoint facts interact soundly with overload resolution.

- [ ] Update lowering and LLVM emission.
  - [ ] Lower required runtime range checks.
  - [ ] Preserve compile-time range facts in MIR and SSA.
  - [ ] Preserve runtime-dependent range facts where useful for LLVM `!range` metadata.
  - [ ] Ensure exponentiation endpoint expressions do not introduce accidental expensive runtime paths unless explicitly required.

- [ ] Update package image support.
  - [ ] Serialize endpoint expressions or normalized endpoint facts for exported typed interfaces.
  - [ ] Deserialize endpoint expressions or facts from package images.
  - [ ] Preserve enough information for imported generic templates and typed bodies.
  - [ ] Add package-image tests for constants, type-member bounds, and runtime-dependent endpoints.

- [ ] Update Stark source files and examples.
  - [ ] Replace tedious fully spelled integer bounds in examples where the new syntax improves clarity.
  - [ ] Update standard library Stark source only where it improves readability without hiding important ABI details.
  - [ ] Keep FFI-facing signatures explicit when that makes the ABI contract clearer.

- [ ] Update tests.
  - [ ] Add parser tests for all accepted endpoint expression forms.
  - [ ] Add type-checking diagnostics for invalid endpoint expressions.
  - [ ] Add compile-time evaluation tests for constants, arithmetic, and exponentiation.
  - [ ] Add runtime-check tests for dependent endpoints.
  - [ ] Add LLVM tests for preserved range metadata where proof facts are available.
  - [ ] Add package-image round-trip tests for exported APIs using richer endpoint expressions.

- [ ] Update documentation.
  - [ ] Update `docs/Userfacing/LanguageReference.md`.
  - [ ] Document `min` and `max` as type-relative endpoint names.
  - [ ] Document supported endpoint operators and precedence.
  - [ ] Document named constant endpoints.
  - [ ] Document type-member bounds.
  - [ ] Document `sizeof(Type)`
  - [ ] Document runtime-dependent endpoints, including when runtime checks are inserted.
  - [ ] Document rejected range-sugar forms as intentionally unsupported if needed.

## Explicit Non-Goals

- [ ] Do not add half-open range syntax such as `i64[0 .. length)`.
  - [ ] The mixed `[` and `)` delimiter pairing is intentionally rejected.
  - [ ] The syntax is less consistent with existing Stark integer ranges.
  - [ ] The syntax would make parsing and reading ranges harder.

- [ ] Do not add omitted endpoint sugar such as `i32[.. 100]` or `i32[0 ..]`.

- [ ] Do not add predicate aliases such as `i32[positive]`, `i32[nonnegative]`, or `i32[nonzero]` as part of this task.

- [ ] Do not add predicate-style constraint syntax such as `powerof2`, `multipleof 16`, or `indexof(values)` as part of this task.

- [ ] Do not add collection-index alias syntax as part of this task.

## Design Note

Runtime-dependent endpoints and predicate-style constraints are much more powerful, but they deserve a deliberate design pass because they affect type identity, overload resolution, runtime checks, and proof propagation.
