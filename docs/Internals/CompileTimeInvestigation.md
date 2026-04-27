# Compile-Time Investigation

This note records a research pass into Stark compiler compile times. The goal was to find low-effort, high-reward opportunities that could improve compile times without changing language behavior or reducing functionality.

The investigation was non-invasive: it used timing runs, compiler pipeline logs, and source inspection. No compiler source code was changed as part of this research run.

## Summary

The biggest low-hanging fruit is in module loading. For source-backed projects, imported modules are parsed once during `module-graph`, then loaded and parsed again during `load-modules`. On larger examples, those two passes dominate `--check` time before the compiler reaches most typing, ownership, or lowering work.

The best first implementation candidate is to cache parsed imported source modules between `module-graph` and `load-modules`.

## Measurement Method

The timings below came from the compiler's verbose pipeline logs using:

```powershell
--log-level info --log-verbosity verbose --log-kind pipeline
```

Wall-clock time includes the `dotnet` process and JIT cost for single-process CLI invocations. Pass timings are the compiler's internal `Stopwatch` timings.

The standard library tests were avoided because they are very slow. The investigation used direct compiler runs against examples and the standard library source.

## Measured Shape

| Case | Wall time | Biggest costs |
| --- | ---: | --- |
| `examples\arithmetic\Arithmetic.stark --check` | ~0.9s | process/JIT plus parse/type/semantic work |
| `examples\standard-library\StandardLibrary.stark --check -I stdlib\src` | ~3.8s | `module-graph` 1.64s, `load-modules` 0.92s |
| `examples\standard-library\StandardLibrary.stark --emit-llvm -I stdlib\src` | ~6.0s | module loading 2.49s, `lower-abi` 0.77s, MIR/SSA/const-prop ~1.3s |
| `examples\raylib\Raylib.stark --check -I examples\raylib` | ~2.5s | `module-graph` 0.75s, `load-modules` 0.41s |
| `stdlib\src\System.stark --emit-pkg -I stdlib\src` | ~7.7s | module loading 3.0s, `lower-abi` 1.06s, MIR/SSA/const-prop ~1.63s |

## Findings And Suggestions

### 1. Cache Parsed Source Modules Between `module-graph` And `load-modules`

This is the strongest first candidate.

Current behavior:

- `ResolveModuleGraphPass` parses imported source modules in `src/Compiler/DefaultCompilerPipeline.cs` with `StarkSyntax.ParseCompilationUnit(sourceText)` while discovering nested imports.
- `LoadModulesPass` then resolves and parses the same source modules again before building loaded module documents.

Observed source references:

- `src/Compiler/DefaultCompilerPipeline.cs:259`
- `src/Compiler/DefaultCompilerPipeline.cs:388`

Expected reward:

- Roughly saves the second parse/build cost for source-backed imports.
- Based on measured runs, this could save around ~0.4s on the Raylib check and ~0.9-1.1s on standard-library source-backed runs.

Expected effort:

- Low to moderate.

Risk:

- Low if the cache preserves source text, parse result, syntax model, diagnostics, and file path exactly.

Implementation shape:

- Store discovered source module documents or parse artifacts from `module-graph`.
- Reuse those artifacts in `load-modules` instead of re-reading and reparsing.
- A natural artifact would contain at least `(sourceText, filePath, ParseResult, SyntaxModel)`.

### 2. Avoid Full Parsing During Module Graph Construction

`module-graph` only needs module name and imports, but it currently parses the whole imported file to get them.

Expected reward:

- High. This could remove much of the 0.75-1.9s spent in `module-graph` for source-backed multi-module projects.

Expected effort:

- Medium.

Risk:

- Medium. A lightweight module/import scanner must match the language grammar closely enough around module declarations and imports.

Implementation shape:

- Add a lightweight scanner for module declarations and imports.
- Use the full parser later in `load-modules`, or combine this with the parse cache so full parsing still happens only once.

### 3. Make `--emit-pkg` Stop Before MIR/SSA When Possible

Package emission currently stops after `lower-abi`:

- `src/Compiler/CompilerCli.cs:2294`

However, the pass order places these passes before `lower-abi`:

- `lower-mir`
- `borrow-liveness`
- `lower-ssa`
- `cleanup-ssa`
- `const-prop`

The `lower-abi` pass itself depends on HIR/type/effects/layout artifacts, not MIR/SSA:

- `src/Compiler/DefaultCompilerPipeline.cs:3481`

Measured reward:

- In the standard-library package-emission run, the avoidable-looking MIR/SSA/const-prop work totaled about ~1.6s.

Expected effort:

- Low to medium if package image generation truly does not need MIR/SSA artifacts.
- Medium if the pipeline needs a dependency-driven stop mode or phase-specific layout.

Risk:

- Low after verifying `PackageImageBuilder.Create` only requires the artifacts it appears to require:
  - loaded modules
  - module graph
  - type model
  - enum layout model
  - ABI model
  - function effects
  - optional semantic validation model

Suggested approach:

- Move `lower-abi` earlier if dependencies allow it cleanly.
- Or teach the CLI/pipeline to execute only the dependency closure of a target pass rather than every earlier registered pass.

### 4. Precompute ABI Function Identity Lookup

`AbiLowerer.ResolveFunctionIdentity` scans all loaded module declarations for each function identity lookup.

Observed source references:

- `src/Compiler/AbiLowering.cs:77`
- `src/Compiler/AbiLowering.cs:143`
- `src/Compiler/AbiLowering.cs:154`
- `src/Compiler/AbiLowering.cs:197`

Measured context:

- `lower-abi` took ~0.77s in a standard-library source-backed LLVM run.
- `lower-abi` took ~1.06s in a standard-library package-emission run.

Expected reward:

- Likely noticeable in package/library/codegen paths with many functions.

Expected effort:

- Low.

Risk:

- Low.

Implementation shape:

- Build a dictionary once in `AbiLowerer`, keyed by qualified function name.
- Store `(moduleName, sourceName, visibility)` as the value.
- Replace repeated declaration scans with O(1) lookups.

### 5. Make Package-Image Imports More Parse-Free

Package loading still synthesizes source and parses it when constructing module documents.

Observed source references:

- `src/Compiler/PackageImage/Loader/PackageImageLoader.cs:251`
- `src/Compiler/PackageImage/Loader/PackageImageLoader.cs:278`

Expected reward:

- Medium to high once package images are used heavily.

Expected effort:

- Medium to high.

Risk:

- Higher than source parse caching because downstream compiler stages may still assume loaded package modules have parse results and syntax-like documents.

Implementation shape:

- Make structured package documents truly parse-free for declarations and facts already present in structured package sections.
- Consider a parse-less package document representation or a placeholder parse result where source bodies are not needed.

### 6. Tighten Manifest Discovery

`FileSystemModuleResolver.EnsureManifestIndex` recursively scans every search directory for `*.starkpkg.json`.

Observed source reference:

- `src/Compiler/ModuleResolution.cs:200`

Expected reward:

- Depends on real project search paths.
- Could be a significant first-hit cost if users pass broad directories through `-I` or `STARK_PATH`.

Expected effort:

- Low if package search conventions can be narrowed.

Risk:

- Low to medium, depending on expected package discovery behavior.

Implementation shape:

- Prefer deterministic package-index names or known package locations.
- Avoid recursive `SearchOption.AllDirectories` unless explicitly requested.
- Consider scanning only top-level search directories by default.

## Secondary Observations

- The checked-in `stdlib/dist/libSystem.starkpkg.json` appeared stale or malformed relative to the current package loader during inspection. This blocked clean package-backed timing with the checked-in dist package.
- A freshly emitted temporary package inspected successfully during the research run, but temporary outputs were cleaned up afterward.
- A single Release-build timing sample did not make the module-loading issue disappear, so "just use Release" does not look like the main answer. Using a prebuilt compiler is still better than measuring through `dotnet run`.
- The accessible reexport traversal in `module-graph` appears to perform repeated `imports.Where(...)` scans. This is probably minor at current sizes, but indexing import edges by `FromModule` would be easy.

## Recommended Order

1. Cache parsed imported source modules between `module-graph` and `load-modules`.
2. Precompute ABI function identity lookup.
3. Make `--emit-pkg` avoid MIR/SSA/const-prop if package emission does not need those artifacts.
4. Replace full parsing in `module-graph` with a lightweight import/module scanner.
5. Reduce package-image parse work.
6. Tighten recursive package manifest discovery.

The first item is the best trade-off: it matches the timing data directly, should preserve behavior, and attacks the largest repeated frontend cost without redesigning the compiler.
