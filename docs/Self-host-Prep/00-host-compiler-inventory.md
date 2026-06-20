# Phase 0 - Host Compiler Inventory

Audit basis: the current host compiler is the ground truth for self-hosting.
This inventory covers tracked source under `src/`, `Stark.g4`, the five tracked
test projects under `tests/`, and current stdlib/tooling docs as of 2026-05-27.

## Repository Scope

| Area | Inventory |
|---|---:|
| Host compiler source files | 103 tracked C# files plus `Stark.g4` |
| Host compiler source lines | 178,476 total, including generated ANTLR output |
| Generated parser/visitor files | `src/Parsing/StarkLexer.cs`, `src/Parsing/StarkParser.cs`, `src/Parsing/StarkBaseVisitor.cs`, `src/Parsing/StarkVisitor.cs` |
| Handwritten parser helpers | `src/Parsing/StarkSyntax.cs`, `src/Parsing/TextLiteralDecoder.cs` |
| Test files | 132 tracked C# files across five xUnit projects |
| Test facts/theories | 2,204 `[Fact]`, 12 `[Theory]`, 13 `[InlineData]`, 8 `MemberData` uses |
| Host build project | `src/compiler.csproj`, `net10.0`, nullable enabled, implicit usings enabled |
| External parser runtime | `Antlr4.Runtime.Standard` package, csproj version `4.13.1`; generated files say ANTLR `4.13.2` |

## Complete Source File Manifest

### Build Root

- `Stark.g4`
- `src/Program.cs`
- `src/Properties/AssemblyInfo.cs`
- `src/compiler.csproj`

### Parsing

- `src/Parsing/StarkBaseVisitor.cs`
- `src/Parsing/StarkLexer.cs`
- `src/Parsing/StarkParser.cs`
- `src/Parsing/StarkSyntax.cs`
- `src/Parsing/StarkVisitor.cs`
- `src/Parsing/TextLiteralDecoder.cs`

### Compiler Root

- `src/Compiler/AbiLowering.cs`
- `src/Compiler/AbiLoweringHeuristics.cs`
- `src/Compiler/ArtifactTextRenderer.cs`
- `src/Compiler/CompileTimeExpressionEvaluator.cs`
- `src/Compiler/CompilerArtifacts.cs`
- `src/Compiler/CompilerCli.cs`
- `src/Compiler/CompilerDiagnostics.cs`
- `src/Compiler/CompilerPipeline.cs`
- `src/Compiler/ConstProvenanceFacts.cs`
- `src/Compiler/DeclaredFunctionSyntax.cs`
- `src/Compiler/DefaultCompilerPipeline.cs`
- `src/Compiler/EnumLayoutBuilder.cs`
- `src/Compiler/FunctionGenericParameterFacts.cs`
- `src/Compiler/FunctionOptimizationSummaryBuilder.cs`
- `src/Compiler/FunctionOverloads.cs`
- `src/Compiler/GenericTemplateBodyComplexityEstimator.cs`
- `src/Compiler/GlobalSymbolNaming.cs`
- `src/Compiler/IntegerRangeStorageFacts.cs`
- `src/Compiler/InterpolatedText.cs`
- `src/Compiler/LlvmIrEmitter.cs`
- `src/Compiler/LlvmTargetInfo.cs`
- `src/Compiler/LoweringContractValidation.cs`
- `src/Compiler/ModuleResolution.cs`
- `src/Compiler/NativeToolchain.cs`
- `src/Compiler/NonLexicalBorrowLifetimeValidation.cs`
- `src/Compiler/OwnershipValidation.cs`
- `src/Compiler/ParameterMemoryContractFacts.cs`
- `src/Compiler/ProjectCliDriver.cs`
- `src/Compiler/SemanticValidation.cs`
- `src/Compiler/SsaIrValidation.cs`
- `src/Compiler/SsaLowering.cs`
- `src/Compiler/SsaOptimization.cs`
- `src/Compiler/StarkAsmArchitectureFacts.cs`
- `src/Compiler/StarkAsmRegisterFacts.cs`
- `src/Compiler/StarkTypeResolver.cs`
- `src/Compiler/SyntaxModelFactory.cs`
- `src/Compiler/TextFormattingFacts.cs`
- `src/Compiler/TypeChecking.cs`
- `src/Compiler/TypeCompatibilityFacts.cs`

### LLVM IR Emission

- `src/Compiler/LlvmIrEmission/LlvmAggregateEmissionSupport.cs`
- `src/Compiler/LlvmIrEmission/LlvmBuiltinAndHelperEmitter.cs`
- `src/Compiler/LlvmIrEmission/LlvmDebugMetadataEmitter.cs`
- `src/Compiler/LlvmIrEmission/LlvmEmissionContext.cs`
- `src/Compiler/LlvmIrEmission/LlvmEmissionContextBuilder.cs`
- `src/Compiler/LlvmIrEmission/LlvmFunctionAttributeBuilder.cs`
- `src/Compiler/LlvmIrEmission/LlvmFunctionBodyEmitter.cs`
- `src/Compiler/LlvmIrEmission/LlvmFunctionSignatureBuilder.cs`
- `src/Compiler/LlvmIrEmission/LlvmGlobalInitializerPlanner.cs`
- `src/Compiler/LlvmIrEmission/LlvmModuleSurfaceEmitter.cs`
- `src/Compiler/LlvmIrEmission/LlvmSpecializationEmissionPlanner.cs`
- `src/Compiler/LlvmIrEmission/LlvmStringConstantEmission.cs`
- `src/Compiler/LlvmIrEmission/LlvmTextOptimizationConstants.cs`
- `src/Compiler/LlvmIrEmission/LlvmValueRangeFacts.cs`
- `src/Compiler/LlvmIrEmission/UnsupportedBodyEmissionException.cs`

### LLVM Function Body Emitter

- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.AbiAndStorage.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Aggregates.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Analysis.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Assumptions.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Calls.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.ControlFlow.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.DynamicStorageAndAddresses.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Instructions.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.LocalsAndAlignment.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Metadata.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.RawPointerLoops.cs`
- `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Utilities.cs`

### MIR Lowering

- `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.cs`
- `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.cs`
- `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.CompileTimeEvaluator.cs`
- `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.ImportedTemplateLowerer.cs`
- `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.PlaceLowerer.cs`
- `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.RuntimeDropLowerer.cs`
- `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.SwitchPatternLowerer.cs`

### Package Image

- `src/Compiler/PackageImage/Bridge/PackageImageSourceBridge.cs`
- `src/Compiler/PackageImage/Builder/CompilerFactsSectionBuilder.cs`
- `src/Compiler/PackageImage/Builder/GenericTemplateSectionBuilder.cs`
- `src/Compiler/PackageImage/Builder/PackageImageBuilder.cs`
- `src/Compiler/PackageImage/Builder/SourceSurfaceSectionBuilder.cs`
- `src/Compiler/PackageImage/Builder/TypedInterfaceSectionBuilder.cs`
- `src/Compiler/PackageImage/Loader/CompilerFactsSectionLoader.cs`
- `src/Compiler/PackageImage/Loader/GenericTemplateSectionLoader.cs`
- `src/Compiler/PackageImage/Loader/PackageImageLoader.cs`
- `src/Compiler/PackageImage/Loader/TypedInterfaceSectionLoader.cs`
- `src/Compiler/PackageImage/Models/PackageImageModels.cs`
- `src/Compiler/PackageImage/Shared/GenericTemplatePublicationPolicy.cs`
- `src/Compiler/PackageImage/Shared/PackageEnumLayoutCodec.cs`
- `src/Compiler/PackageImage/Shared/PackageTypeCodec.cs`

### SSA Optimization

- `src/Compiler/SsaOptimization/SsaAddressTakenFunctionPruner.cs`
- `src/Compiler/SsaOptimization/SsaAggregateConstructionStoreOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaAliasAwareMemoryOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaAsciiToUnicodeLiteralSpecializer.cs`
- `src/Compiler/SsaOptimization/SsaCleanupOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaConstGraphCallCseOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaConstLookupTableOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaConstStdlibHelperSpecializer.cs`
- `src/Compiler/SsaOptimization/SsaConstantPropagator.cs`
- `src/Compiler/SsaOptimization/SsaConstantTextFormatSpecializer.cs`
- `src/Compiler/SsaOptimization/SsaDirectCallDevirtualizer.cs`
- `src/Compiler/SsaOptimization/SsaDirectCallInliner.cs`
- `src/Compiler/SsaOptimization/SsaDynamicAppendLoopOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaDynamicStorageCallFactPolicy.cs`
- `src/Compiler/SsaOptimization/SsaDynamicStorageOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaFactDrivenBranchPruner.cs`
- `src/Compiler/SsaOptimization/SsaIntegerArithmeticFolder.cs`
- `src/Compiler/SsaOptimization/SsaOwnershipTrafficOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaScalarReplacementOptimizer.cs`
- `src/Compiler/SsaOptimization/SsaValueFactAnalyzer.cs`

## Host Technology Stack

| Dependency | Where Used | Self-host Impact |
|---|---|---|
| C# records/classes/interfaces/partial classes | Most compiler artifacts and pass implementations, especially `src/Compiler/CompilerArtifacts.cs`, `src/Compiler/CompilerPipeline.cs`, generated parser classes | Stark port must model large immutable records, mutable pass state, interfaces such as `ICompilerPass`, and generated/nested parser shapes without C# partial classes |
| .NET collections | `Dictionary`, `HashSet`, `List`, `Queue`, `Stack`, `IReadOnlyList`, `IReadOnlyDictionary`, `IReadOnlySet` throughout `src/Compiler` | Requires Stark collection parity through `Hash` + `Eq` / `Ord` contracts, text-key support, hash sets, deterministic output ordering, and compiler-internal typed interning for hot symbol/name paths |
| LINQ and lambdas | Sorting, filtering, projection, grouping, pass ordering, diagnostics, tests | Requires iterators/loops/helpers or explicit rewrites |
| `System.Numerics.BigInteger` | `src/Compiler/TypeChecking.cs`, `src/Compiler/CompileTimeExpressionEvaluator.cs`, `src/Compiler/IntegerRangeStorageFacts.cs`, `src/Compiler/EnumLayoutBuilder.cs`, SSA value facts | Host implementation convenience only; self-host decision OQ-07 replaces it with bounded compiler-internal `i1024`/`u1024` integer-fact helpers and diagnostics for overflow/oversized values |
| `System.Text.StringBuilder` and `Encoding` | LLVM/MIR/SSA rendering, text literal decoding, diagnostics | Requires owned text builders, escaping, UTF-8/UTF-16/UTF-32 helpers |
| `System.Text.Json` | Current host package image load/write and CLI inspection | Self-host decision OQ-09/doc `20` replaces normal package loading with binary package images; deterministic JSON/text remains for inspection/export and tests |
| `System.Text.RegularExpressions` | `ProjectCliDriver`, tests and golden-ish assertions | Requires Regex, simpler pattern helpers, or test rewrites |
| `System.IO`, `System.Diagnostics.Process`, `Environment`, `OperatingSystem` | CLI, project driver, native toolchain, tests | Requires file, path, process, env, temp dir, platform, and tool capture APIs |
| xUnit | All current tests | Requires Stark test runner and assertion replacement before tests can be ported |

## Explicit Host Imports

Top explicit `using` imports across `src` and tracked tests, excluding build
output:

| Namespace | Count | Notes |
|---|---:|---|
| `Stark.Compiler` | 111 | Internal compiler model used by tests |
| `Stark.Parsing` | 80 | Parser tests and compiler front end |
| `System.Numerics` | 58 | Range-typed integers, enum tags, constants |
| `System.Text` | 44 | builders, encodings |
| `System.Globalization` | 42 | invariant numeric/text formatting |
| `Antlr4.Runtime` | 16 | parser entry points and generated classes |
| `System.Text.RegularExpressions` | 8 | project parsing/tests |
| `Antlr4.Runtime.Tree` | 8 | parse tree walking |
| `System.Diagnostics` | 7 | process execution and stopwatches |
| `System.Text.Json` | 4 | package image JSON |
| `System.Runtime.CompilerServices` | 4 | assembly internals/testing |
| `System.IO` | 3 | explicit file/stream APIs, plus implicit usage |

## Subsystem Inventory

### Build Entry, CLI, Project Driver

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Program.cs`, `src/compiler.csproj`, `src/Compiler/CompilerCli.cs`, `src/Compiler/ProjectCliDriver.cs`, `src/Compiler/NativeToolchain.cs`, `src/Compiler/LlvmTargetInfo.cs` | CLI modes, project/solution discovery, target option parsing, diagnostics, native compilation/linking, package/native metadata collection, tool metrics | `async Task`, nullable refs, records, dictionaries/sets/lists, regex, JSON, exceptions, process capture, temp dirs | `System.IO`, `System.Diagnostics.Process`, `System.Text`, `System.Text.Json`, `Regex`, `Environment`, `OperatingSystem` | `dotnet`, generated repo launcher, `chmod`, `clang`, linker, archiver, `pkg-config`, platform SDK tools | CLI args, `Stark.toml`, `Stark.solution.toml`, `Stark.user.toml`, `.starkpkg.json`, `.ll`, `.o/.obj`, `.a/.lib`, executable, tool metrics text |

### Grammar, Lexer, Parser, Parse Facade

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `Stark.g4`, `src/Parsing/StarkLexer.cs`, `src/Parsing/StarkParser.cs`, `src/Parsing/StarkVisitor.cs`, `src/Parsing/StarkBaseVisitor.cs`, `src/Parsing/StarkSyntax.cs`, `src/Parsing/TextLiteralDecoder.cs` | Stark grammar, tokenization, parse trees, parse diagnostics, text literal validation/decoding, parser facade for compilation units/expressions | ANTLR generated partial classes, visitors, exceptions, `StringBuilder`, UTF encoders/decoders, arrays | `Antlr4.Runtime`, `Antlr4.Runtime.Tree`, `System.Text`, `System.Globalization`, `System.IO` | ANTLR generator/runtime | `.stark` source text, token streams, parse trees, text literal escapes |

### Syntax Model, Modules, Names, Type Resolution

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Compiler/SyntaxModelFactory.cs`, `src/Compiler/DeclaredFunctionSyntax.cs`, `src/Compiler/ModuleResolution.cs`, `src/Compiler/StarkTypeResolver.cs`, `src/Compiler/FunctionOverloads.cs`, `src/Compiler/FunctionGenericParameterFacts.cs`, `src/Compiler/GlobalSymbolNaming.cs`, `src/Compiler/TypeCompatibilityFacts.cs` | Convert parse trees to declaration models, collect functions/destructors, resolve imports and package-backed modules, resolve named/alias/generic/function-pointer/closure types, overload selection, symbol naming | Parse tree casts, records, dictionaries, sets, string comparers, LINQ, nullable facts | `Stark.Parsing`, `Antlr4.Runtime`, collection APIs | File system module resolution | Stark source modules, package image module surfaces, declaration and type model artifacts |

### Type Checking, Semantic Validation, Ownership, Borrowing

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Compiler/TypeChecking.cs`, `src/Compiler/SemanticValidation.cs`, `src/Compiler/OwnershipValidation.cs`, `src/Compiler/NonLexicalBorrowLifetimeValidation.cs`, `src/Compiler/LoweringContractValidation.cs`, `src/Compiler/ParameterMemoryContractFacts.cs`, `src/Compiler/IntegerRangeStorageFacts.cs`, `src/Compiler/ConstProvenanceFacts.cs`, `src/Compiler/EnumLayoutBuilder.cs`, `src/Compiler/CompileTimeExpressionEvaluator.cs`, `src/Compiler/FunctionOptimizationSummaryBuilder.cs`, `src/Compiler/GenericTemplateBodyComplexityEstimator.cs`, `src/Compiler/TextFormattingFacts.cs` | Range-typed integer checking, generic instantiation, aliases, traits/doctrines, function kinds (`finite`, `law`), raw pointer safety, memory contracts (`disjoint`, `overlap`, `same`), borrow classes (`borrow`, `retborrow`, `storeborrow`), const provenance, switch coverage, enum layout, ownership/move/drop, non-lexical borrow checks, lowering contract validation | Very large mutable pass state, nested records/classes, `.NET BigInteger` arithmetic as host convenience, dictionaries/sets, switch pattern matching, LINQ, exceptions for invariants | `System.Numerics`, `System.Globalization`, ANTLR parser contexts | None directly | Compiler artifacts, diagnostics, typed facts, enum layout facts, alias/noalias facts |

### Pipeline, Artifacts, Diagnostics, Logging

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Compiler/CompilerArtifacts.cs`, `src/Compiler/CompilerPipeline.cs`, `src/Compiler/DefaultCompilerPipeline.cs`, `src/Compiler/CompilerDiagnostics.cs`, `src/Compiler/ArtifactTextRenderer.cs` | Artifact store, pass graph/dependencies, default 43-pass pipeline, diagnostics/log data, text rendering of MIR/SSA, compiler artifact validation models | Records for IR/artifacts, interface dispatch, dictionaries, topological sort, `StringBuilder`, `Stopwatch`, exceptions, scoped disposable log context | `System.Diagnostics`, `System.Text`, collections | None directly | Compiler log records, diagnostics, MIR/SSA textual dumps, pass execution records |

### HIR/MIR Lowering

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.cs`, `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.cs`, `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.CompileTimeEvaluator.cs`, `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.ImportedTemplateLowerer.cs`, `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.PlaceLowerer.cs`, `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.RuntimeDropLowerer.cs`, `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.SwitchPatternLowerer.cs` | Lower accepted source and imported template bodies to MIR, place/address lowering, runtime drop lowering, switch pattern lowering, compile-time template evaluation, generic imported template lowering | Partial/nested classes, large mutable builders, stacks/lists/dictionaries, records, `.NET BigInteger` host convenience, generated parser contexts, scoped name aliases | ANTLR contexts, compiler artifacts | None directly | MIR artifacts, imported template body representation, typed template facts |

### SSA Lowering, Validation, Optimizations

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Compiler/SsaLowering.cs`, `src/Compiler/SsaIrValidation.cs`, `src/Compiler/SsaOptimization.cs`, `src/Compiler/SsaOptimization/*.cs` | MIR to SSA, phi construction, SSA validation, cleanup, constant propagation, direct-call devirtualization/inlining, const graph CSE, lookup-table folding, value facts, dynamic storage optimizations, path/text specializers, branch pruning, alias-aware memory optimization, aggregate construction, ownership traffic removal, SROA, arithmetic folding | Nested builders, records, dictionaries/sets, graph walks, `.NET BigInteger` host convenience, LINQ, alias/noalias metadata, function-effect summaries | Compiler artifacts, `System.Numerics` | None directly | SSA artifacts, optimized SSA artifacts, value-fact models, scoped noalias groups |

### ABI Lowering and LLVM Text Emission

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Compiler/AbiLowering.cs`, `src/Compiler/AbiLoweringHeuristics.cs`, `src/Compiler/LlvmIrEmitter.cs`, `src/Compiler/LlvmIrEmission/*.cs`, `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/*.cs` | ABI model, LLVM module/function signatures, helper/runtime emission, global initialization, debug metadata, function body emission, aggregate/text/dynamic storage/address/call/control-flow emission, scoped noalias metadata, target features | Records, dictionaries/sets, `StringBuilder`, `.NET BigInteger` host convenience, target-specific branches, exceptions for unsupported accepted programs | `System.Text`, compiler artifacts | LLVM toolchain consumes output | Textual LLVM IR, debug metadata text, object/static/executable artifacts downstream |

### Package Image

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Compiler/PackageImage/Models/PackageImageModels.cs`, `src/Compiler/PackageImage/Builder/*.cs`, `src/Compiler/PackageImage/Loader/*.cs`, `src/Compiler/PackageImage/Bridge/PackageImageSourceBridge.cs`, `src/Compiler/PackageImage/Shared/*.cs` | Build/load current-host `.starkpkg.json`, source-surface, typed-interface, compiler-facts, generic-templates, native dependency metadata, legacy source bridge, type/enum-layout codecs | JSON records, lists/dictionaries, LINQ, nullable data, string rendering and parsing | `System.Text.Json` through CLI/package image paths | None directly | Current-host `.starkpkg.json`; self-hosted binary package image plus JSON/text inspection artifacts; library filenames, structured package sections, native metadata |

### Assembly, Target, Native Facts

| Source Paths | Major Responsibilities | Host Features Used | Imports / APIs | External Tools | File Formats |
|---|---|---|---|---|---|
| `src/Compiler/StarkAsmArchitectureFacts.cs`, `src/Compiler/StarkAsmRegisterFacts.cs`, `src/Compiler/LlvmTargetInfo.cs`, parts of `src/Compiler/NativeToolchain.cs` | Validate inline assembly architecture/register classes, target triples/data layout, relocation/code model/CPU/features, default target probing | Static sets, platform conditionals, process execution, string parsing | `OperatingSystem`, `Process`, `Path`, `Directory`, `File` | `clang`, linker, archiver, macOS SDK tools | Inline asm strings, LLVM target triple/data layout, target options |

## External Tool Inventory

| Tool | Invoked From | Purpose | Required for Self-hosted Compiler |
|---|---|---|---|
| `dotnet` | repo-root generated launcher from `src/compiler.csproj` | Host compiler launch today | No after self-hosting, yes during bootstrap |
| `chmod` | `src/compiler.csproj` build target | Make repo-root launcher executable | No after packaging decision |
| ANTLR generator | Generated `src/Parsing/*` from `Stark.g4`; generator version shown as `4.13.2` | Parser source generation | Decision required: keep generated parser, port runtime, or replace parser |
| `clang` | `src/Compiler/NativeToolchain.cs` | LLVM IR to object/executable, native C shim compile, default target detection | Yes unless Stark binds LLVM directly |
| `ld.lld` / `lld-link` | `NativeToolchain.SupportsExecutableThinLto`, linker selection | ThinLTO and linking | Yes if textual LLVM and bundled toolchain path continue |
| `llvm-ar`, `llvm-lib`, `ranlib`-style tools | static library creation paths | Build `.a` / `.lib` | Yes for library/package output |
| `pkg-config` | `CompilerCli` native dependency discovery | Native package metadata resolution | Yes if package-owned native deps keep discovery |
| platform SDK tools/libs | native link on Linux/macOS/Windows | CRT/libc/SDK resolution | Yes, even with bundled LLVM |

## File Format Inventory

| Format | Producers | Consumers | Notes |
|---|---|---|---|
| Stark source `.stark` | Users, stdlib, tests | Parser/front end | Must remain accepted by host and self-hosted compiler during bootstrap |
| `Stark.toml` | Projects | `ProjectCliDriver` | Parsed by host `SimpleToml`; self-hosting replaces this with reusable `System.Toml` plus typed manifest decoding |
| `Stark.solution.toml` | Solutions | `ProjectCliDriver` | Build/run/test target discovery through `System.Toml` plus typed solution decoding |
| `Stark.user.toml` / user config TOML | Local config | `ProjectCliDriver` | Tool/native path overrides through `System.Toml` plus typed user-config decoding |
| `.starkpkg.json` | Current host package image builder | Current host package image loader, CLI inspector | Compiler-owned JSON package image today; self-hosting moves normal loading to binary package images and keeps deterministic JSON/text for inspection/export |
| Textual LLVM IR `.ll` | LLVM emitter | `clang` | Primary codegen artifact today |
| Native object `.o/.obj` | `clang` | linker/archiver | Build artifacts |
| Static library `.a/.lib` | archiver | downstream linker/package consumers | Package library output |
| Executable | linker | `stark run`, integration tests | Native run artifact |
| MIR/SSA text | `ArtifactTextRenderer`, CLI modes | tests/debugging | Snapshot/golden-style assertions depend on stable text |
| Toolchain metrics text | `CompilerCli` | tests/diagnostics | Needs stable format or machine-readable replacement |

## Compile-time Alias / Noalias Tracking

The host compiler does track alias-related facts at compile time. The current
source-level contracts are `disjoint`, `overlap`, `same`, `if disjoint(...)`,
and `unsafe assume disjoint(...)`; they are validated in `src/Compiler/TypeChecking.cs`
and summarized by `src/Compiler/ParameterMemoryContractFacts.cs`.

The lowered fact carrier is `ScopedNoAliasGroup` in
`src/Compiler/CompilerArtifacts.cs`, which is attached through MIR/SSA by
`src/Compiler/SsaLowering.cs`, consumed by SSA optimizers such as
`src/Compiler/SsaOptimization/SsaAliasAwareMemoryOptimizer.cs`, and emitted as
LLVM scoped noalias metadata by `src/Compiler/LlvmIrEmission/LlvmDebugMetadataEmitter.cs`,
`src/Compiler/LlvmIrEmission/LlvmEmissionContext.cs`, and
`src/Compiler/LlvmIrEmission/FunctionBodyEmitter/*.cs`.

For self-hosting, these facts must remain compiler artifacts, not informal code
comments. Wrong alias-class use must be reported before lowering or SSA
validation, not left as backend undefined behavior.
