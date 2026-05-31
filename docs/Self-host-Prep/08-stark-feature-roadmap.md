# Stark Feature Roadmap For Self-hosting

Status: WIP. This is a living task list for language and stdlib features that
help Stark become a practical implementation language for its own compiler.

The main design pressure is reuse without hidden behavior. Stark should keep
runtime costs, dispatch, allocation, aliasing, and failure explicit.

## Compile-time Reuse

- [ ] Strengthen compile-time-only `trait` and `doctrine` support.
- [ ] Add default method bodies for compile-time-only traits/doctrines.
- [ ] Add associated types for compile-time contracts.
- [ ] Define doctrine-based `Hash`, `Eq`, `Ord`, and `Format` style contracts.
- [ ] Keep traits/doctrines as compile-time contracts, not runtime objects.
- [ ] Do not add hidden trait objects, hidden vtables, or implicit dynamic dispatch.

Notes:

Default method bodies, associated types, and doctrine-based `Hash`, `Eq`,
`Ord`, and `Format` contracts would make generic compiler code much easier to
write without adding trait objects. The compiler can still resolve these
contracts statically and emit concrete code.

Useful self-hosting targets:

- `Dictionary<K, V>` keys for `ascii`, `unicode`, symbols, and interned names.
- Generic equality and ordering for deterministic compiler output.
- Generic formatting for diagnostics, logging, package images, and LLVM text.
- Reusable collection algorithms without a runtime dispatch layer.

## Explicit Runtime Dispatch

- [ ] Add a blessed pattern for explicit runtime dispatch using ops tables.
- [ ] Keep ops tables visible in source as ordinary structs.
- [ ] Require dispatch function pointers to spell their function kind, such as
      `fn`, `finite`, `law`, or `finite law`.
- [ ] Make any type-erased context pointer or unsafe boundary explicit.
- [ ] Prefer closed-world enums when the set of implementations is known.
- [ ] Reserve ops tables for genuinely open runtime extension points.

An explicit ops table is basically a vtable, but it is not hidden. The caller
can see the context pointer, the ops table, and the function pointer types.
That keeps the cost model and safety boundary Stark-shaped.

Example shape:

```stark
struct ModuleResolverOps
{
    Resolve: fnptr<fn ResolveResult(
        rawmutptr<i8[min max]> context,
        ascii moduleName)>;
}

struct ModuleResolverHandle
{
    Context: rawmutptr<i8[min max]>;
    Ops: ModuleResolverOps;
}
```

Open questions for this pattern:

- [ ] Should the context pointer always be raw, or should Stark offer a typed
      erased-handle wrapper?
- [ ] Should ops tables be `const` by convention?
- [ ] Should ops functions carry explicit memory contracts such as
      `where disjoint(...)` when they touch caller buffers?
- [ ] Should there be a standard naming convention: `FooOps`, `FooHandle`,
      `FooContext`?

## Closed-world Runtime Choice

- [ ] Prefer `enum` plus exhaustive `switch` for runtime variation when all
      implementations are known.
- [ ] Use this for compiler-internal choices such as module resolver kind,
      diagnostic output mode, package section kind, target platform, and pass
      kind where practical.

Example shape:

```stark
enum ModuleResolver
{
    Empty(EmptyModuleResolver),
    FileSystem(FileSystemModuleResolver),
    InMemory(InMemoryModuleResolver),
    Package(PackageImageResolver),
}
```

This keeps dispatch visible and gives the compiler exhaustiveness checks.

## Error And Optional Values

- [ ] Define shared `Option<T>` and `Result<T, E>` conventions.
- [ ] Decide whether Stark gets a `?`-style propagation operator.
- [ ] Add a compiler-invariant failure API for explicit trap/abort paths.

Self-hosting needs a replacement for C# nullability, exceptions, and
`TryGet(... out value)` patterns. Recoverable failures should remain values.
Internal compiler bugs should have an explicit, documented failure path.

## Compiler-grade Standard Library

- [ ] String-key dictionaries for `ascii` and `unicode`.
- [ ] `HashSet<T>`.
- [ ] Symbol interning.
- [ ] Deterministic sorting and ordered set/map helpers.
- [ ] Text builder and formatting APIs.
- [ ] JSON support for `.starkpkg.json`, unless the package image format changes.
- [ ] TOML support for `Stark.toml` and `Stark.solution.toml`.
- [ ] File read-all/write-all helpers.
- [ ] Temp directory helpers.
- [ ] Process spawn with stdout/stderr capture.
- [ ] Environment and argv APIs.

## Memory Model Work

- [ ] Decide compiler IR storage strategy: arena plus handles, explicit shared
      ownership, or owned trees plus cross-reference tables.
- [ ] Keep `arena` explicit if it becomes an executable storage class.
- [ ] Keep unsafe shared ownership visible in source.

Likely direction for the compiler: arena or owned collections with stable
handles, not a hidden garbage-collected object graph.
