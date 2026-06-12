# Phase 5 - Port Checklist

Migration target root: when the self-hosted compiler source is introduced, the
current C# host compiler directory moves from `/src` to `/old_src`, and the
Stark compiler source is created in the new `/src`. This is the resolved OQ-01
decision in `07-open-questions.md`.

Test target paths still use `tests-stark/` as a planning convention until the
test-layout cutover is decided.

In compiler tables, `Source Path` names the current host compiler file before
the migration rename. After cutover, those same host files live under
`/old_src/...`.

Effort scale:

- S: small mechanical port or wrapper
- M: focused subsystem
- L: large subsystem/test family
- XL: very large or strategy-dependent subsystem

## Compiler Source Checklist

### Grammar and Parsing

Decision: `Stark.g4` remains the canonical grammar reference, but the
self-hosted compiler uses a handwritten parser. Do **not** port generated ANTLR
artifacts (`src/Parsing/StarkLexer.cs`, `StarkParser.cs`, `StarkVisitor.cs`,
`StarkBaseVisitor.cs`) as generated-code shapes.

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `Stark.g4` | `Stark.g4` canonical reference | S | none | T01 |
| - [ ] | new handwritten lexer, informed by `Stark.g4` and `src/Parsing/StarkLexer.cs` behavior | `src/Parsing/StarkLexer.stark` | L | canonical grammar, parser tests | T01, S02, S03 |
| - [ ] | new handwritten parser, informed by `Stark.g4` and `src/Parsing/StarkParser.cs` behavior | `src/Parsing/StarkParser.stark` | XL | handwritten lexer, parser tests | T01, L04, L12 |
| - [ ] | new Stark-native syntax tree / parse-event model | `src/Parsing/StarkSyntaxTree.stark` | L | handwritten parser | T01, L12, S06 |
| - [ ] | `src/Parsing/StarkSyntax.cs` parser facade behavior | `src/Parsing/StarkSyntax.stark` | M | handwritten lexer/parser | T01, S02, S03, S18 |
| - [ ] | `src/Parsing/TextLiteralDecoder.cs` | `src/Parsing/TextLiteralDecoder.stark` | M | `System.Text` builders/encoding | S02, S03, S05 |

### Entry Point, CLI, Project Driver, Native Toolchain

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Program.cs` | `src/Program.stark` | S | `CompilerCli` | T02, T03, S12 |
| - [ ] | `src/Properties/AssemblyInfo.cs` | `src/Build/AssemblyInfoReplacement.stark` | S | build metadata decision | T14 |
| - [ ] | `src/compiler.csproj` | `src/Stark.toml` | M | formal build layout from `25-build-artifact-layout.md` | T02, T03, T05, T11, OQ-01 |
| - [ ] | `src/Compiler/CompilerCli.cs` | `src/Compiler/CompilerCli.stark` | XL | pipeline, native toolchain, package image | L01, L10, L11, S09, S10, S11, S12, S14, T03, T07, T08, T15 |
| - [ ] | `src/Compiler/ProjectCliDriver.cs` | `src/Compiler/ProjectCliDriver.stark` | XL | compiler CLI, native toolchain | L01, L10, L11, S10, S11, S12, S13, T03, T04, T05 |
| - [ ] | `src/Compiler/NativeToolchain.cs` | `src/Compiler/NativeToolchain.stark` | L | process/path/file stdlib | L01, S09, S10, S11, S12, S15, T08, T09, T10 |
| - [ ] | `src/Compiler/LlvmTargetInfo.cs` | `src/Compiler/LlvmTargetInfo.stark` | S | none | T09 |

### Core Artifacts, Diagnostics, Pipeline

Decision: compiler IR graphs and graph-shaped artifacts use arena/table storage
with typed handles plus first-class fact tables. See
`24-ir-memory-and-fact-model.md`.

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | new shared IR table, typed-handle, and fact-table infrastructure | `src/Compiler/IrStorage.stark` / `src/Compiler/CompilerFacts.stark` | XL | collections, bounded integer facts, package-image fact model | L13, S05, S06, S17, T07 |
| - [ ] | `src/Compiler/CompilerArtifacts.cs` | `src/Compiler/CompilerArtifacts.stark` | XL | collections/text/bounded integer-fact conventions | L01, L06, L11, L12, L13, S01, S02, S05, S06, S17 |
| - [ ] | `src/Compiler/CompilerDiagnostics.cs` | `src/Compiler/CompilerDiagnostics.stark` | L | text builder/result conventions | L01, S01, S02, S15, TEST-12 |
| - [ ] | `src/Compiler/CompilerPipeline.cs` | `src/Compiler/CompilerPipeline.stark` | L | artifacts/diagnostics | L01, L07, L11, S06, S15 |
| - [ ] | `src/Compiler/DefaultCompilerPipeline.cs` | `src/Compiler/DefaultCompilerPipeline.stark` | XL | all pass constructors | L03, S06, T03, T05 |
| - [ ] | `src/Compiler/ArtifactTextRenderer.cs` | `src/Compiler/ArtifactTextRenderer.stark` | M | artifacts/text builder | S02, S03, TEST-03 |

### Front-end Models, Modules, Names, Types

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/SyntaxModelFactory.cs` | `src/Compiler/SyntaxModelFactory.stark` | L | parser facade, artifacts | L01, L04, L11, S06, T01 |
| - [ ] | `src/Compiler/DeclaredFunctionSyntax.cs` | `src/Compiler/DeclaredFunctionSyntax.stark` | M | parser facade | T01, S06 |
| - [ ] | `src/Compiler/ModuleResolution.cs` | `src/Compiler/ModuleResolution.stark` | L | file/path/package loading | S09, S10, S11, S14, T07, T11 |
| - [ ] | `src/Compiler/StarkTypeResolver.cs` | `src/Compiler/StarkTypeResolver.stark` | L | artifacts, type aliases | L01, L06, L11, S05, S06 |
| - [ ] | `src/Compiler/FunctionOverloads.cs` | `src/Compiler/FunctionOverloads.stark` | M | type model | L03, L06, S06 |
| - [ ] | `src/Compiler/FunctionGenericParameterFacts.cs` | `src/Compiler/FunctionGenericParameterFacts.stark` | S | parser facade | S06 |
| - [ ] | `src/Compiler/GlobalSymbolNaming.cs` | `src/Compiler/GlobalSymbolNaming.stark` | S | text builder | S02 |
| - [ ] | `src/Compiler/InterpolatedText.cs` | `src/Compiler/InterpolatedText.stark` | M | text literal helpers | L08, S02, S03 |
| - [ ] | `src/Compiler/TypeCompatibilityFacts.cs` | `src/Compiler/TypeCompatibilityFacts.stark` | M | type model | L06, L13, S06 |
| - [ ] | `src/Compiler/TextFormattingFacts.cs` | `src/Compiler/TextFormattingFacts.stark` | S | text model | S02, S03 |

### Type Checking, Semantic Validation, Ownership

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/TypeChecking.cs` | `src/Compiler/TypeChecking.stark` | XL | parser, artifacts, type resolver | L01, L02, L04, L06, L07, L11, L13, S01, S05, S06, S07 |
| - [ ] | `src/Compiler/SemanticValidation.cs` | `src/Compiler/SemanticValidation.stark` | XL | type checking artifacts | L01, L02, L04, L06, L07, L11, L13, S05, S06 |
| - [ ] | `src/Compiler/OwnershipValidation.cs` | `src/Compiler/OwnershipValidation.stark` | XL | type model, semantic facts | L01, L03, L07, L11, S06, S17 |
| - [ ] | `src/Compiler/NonLexicalBorrowLifetimeValidation.cs` | `src/Compiler/NonLexicalBorrowLifetimeValidation.stark` | L | MIR, ownership model | L01, L03, S06 |
| - [ ] | `src/Compiler/LoweringContractValidation.cs` | `src/Compiler/LoweringContractValidation.stark` | L | type/semantic facts | L01, L04, L07, L13, S06 |
| - [ ] | `src/Compiler/ParameterMemoryContractFacts.cs` | `src/Compiler/ParameterMemoryContractFacts.stark` | M | type model | L13, S06 |
| - [ ] | `src/Compiler/IntegerRangeStorageFacts.cs` | `src/Compiler/IntegerRangeStorageFacts.stark` | M | bounded `i1024`/`u1024` range helpers | S05 |
| - [ ] | `src/Compiler/ConstProvenanceFacts.cs` | `src/Compiler/ConstProvenanceFacts.stark` | S | type model | L13 |
| - [ ] | `src/Compiler/EnumLayoutBuilder.cs` | `src/Compiler/EnumLayoutBuilder.stark` | M | type model, bounded integer facts | S05, S06 |
| - [ ] | `src/Compiler/CompileTimeExpressionEvaluator.cs` | `src/Compiler/CompileTimeExpressionEvaluator.stark` | L | parser, bounded integer facts | L09, S05 |
| - [ ] | `src/Compiler/FunctionOptimizationSummaryBuilder.cs` | `src/Compiler/FunctionOptimizationSummaryBuilder.stark` | M | parser/type facts | T01, S06 |
| - [ ] | `src/Compiler/GenericTemplateBodyComplexityEstimator.cs` | `src/Compiler/GenericTemplateBodyComplexityEstimator.stark` | M | parser/template facts | T01, S06 |

### HIR/MIR Lowering

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.cs` | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.stark` | XL | artifacts, type model | L01, L07, L11, S06, S17 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.cs` | `src/Compiler/MidLevelIrLowering/FunctionMirBuilder.stark` | XL | MIR lowerer shell | L01, L02, L04, L07, L11, S06, S17 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.CompileTimeEvaluator.cs` | `src/Compiler/MidLevelIrLowering/FunctionMirBuilder.CompileTimeEvaluator.stark` | L | function MIR builder, CT evaluator | L09, S05, S06 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.ImportedTemplateLowerer.cs` | `src/Compiler/MidLevelIrLowering/FunctionMirBuilder.ImportedTemplateLowerer.stark` | XL | package image, template facts | L01, L04, S06, S14, T07 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.PlaceLowerer.cs` | `src/Compiler/MidLevelIrLowering/FunctionMirBuilder.PlaceLowerer.stark` | XL | function MIR builder | L07, L13, S06 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.RuntimeDropLowerer.cs` | `src/Compiler/MidLevelIrLowering/FunctionMirBuilder.RuntimeDropLowerer.stark` | L | ownership/type facts | L07, S17 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.SwitchPatternLowerer.cs` | `src/Compiler/MidLevelIrLowering/FunctionMirBuilder.SwitchPatternLowerer.stark` | XL | switch/type facts | L04, S06 |

### SSA Lowering, Validation, Optimization

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/SsaLowering.cs` | `src/Compiler/SsaLowering.stark` | XL | MIR artifacts | L07, L13, S06, S17 |
| - [ ] | `src/Compiler/SsaIrValidation.cs` | `src/Compiler/SsaIrValidation.stark` | L | SSA artifacts | L07, L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization.cs` | `src/Compiler/SsaOptimization.stark` | M | SSA optimizer files | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaAddressTakenFunctionPruner.cs` | `src/Compiler/SsaOptimization/SsaAddressTakenFunctionPruner.stark` | M | SSA artifacts | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaAggregateConstructionStoreOptimizer.cs` | `src/Compiler/SsaOptimization/SsaAggregateConstructionStoreOptimizer.stark` | L | SSA artifacts | L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaAliasAwareMemoryOptimizer.cs` | `src/Compiler/SsaOptimization/SsaAliasAwareMemoryOptimizer.stark` | L | SSA value/effect facts | L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaAsciiToUnicodeLiteralSpecializer.cs` | `src/Compiler/SsaOptimization/SsaAsciiToUnicodeLiteralSpecializer.stark` | L | SSA value facts, text helpers | S02, S03, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaCleanupOptimizer.cs` | `src/Compiler/SsaOptimization/SsaCleanupOptimizer.stark` | XL | SSA artifacts | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstGraphCallCseOptimizer.cs` | `src/Compiler/SsaOptimization/SsaConstGraphCallCseOptimizer.stark` | L | function effects, SSA | L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstLookupTableOptimizer.cs` | `src/Compiler/SsaOptimization/SsaConstLookupTableOptimizer.stark` | M | const initializer facts | S05, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstStdlibHelperSpecializer.cs` | `src/Compiler/SsaOptimization/SsaConstStdlibHelperSpecializer.stark` | L | SSA value facts, path/text | S02, S11, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstantPropagator.cs` | `src/Compiler/SsaOptimization/SsaConstantPropagator.stark` | L | SSA artifacts, bounded integer facts | S05, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstantTextFormatSpecializer.cs` | `src/Compiler/SsaOptimization/SsaConstantTextFormatSpecializer.stark` | L | text formatting/value facts | S02, S03, S05 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDirectCallDevirtualizer.cs` | `src/Compiler/SsaOptimization/SsaDirectCallDevirtualizer.stark` | M | callable facts | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDirectCallInliner.cs` | `src/Compiler/SsaOptimization/SsaDirectCallInliner.stark` | XL | function effects, SSA clone helpers | L03, S06, S17 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDynamicAppendLoopOptimizer.cs` | `src/Compiler/SsaOptimization/SsaDynamicAppendLoopOptimizer.stark` | L | value facts, dynamic storage | S06, S17 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDynamicStorageCallFactPolicy.cs` | `src/Compiler/SsaOptimization/SsaDynamicStorageCallFactPolicy.stark` | S | dynamic storage facts | S17 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDynamicStorageOptimizer.cs` | `src/Compiler/SsaOptimization/SsaDynamicStorageOptimizer.stark` | L | value/effect facts | S06, S17 |
| - [ ] | `src/Compiler/SsaOptimization/SsaFactDrivenBranchPruner.cs` | `src/Compiler/SsaOptimization/SsaFactDrivenBranchPruner.stark` | M | value facts | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaIntegerArithmeticFolder.cs` | `src/Compiler/SsaOptimization/SsaIntegerArithmeticFolder.stark` | L | bounded integer/range facts | S05, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaOwnershipTrafficOptimizer.cs` | `src/Compiler/SsaOptimization/SsaOwnershipTrafficOptimizer.stark` | M | ownership/SSA | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaScalarReplacementOptimizer.cs` | `src/Compiler/SsaOptimization/SsaScalarReplacementOptimizer.stark` | L | alias-aware SSA | L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaValueFactAnalyzer.cs` | `src/Compiler/SsaOptimization/SsaValueFactAnalyzer.stark` | XL | bounded integer facts, SSA facts | L13, S05, S06 |

### ABI and libLLVM Emission

The self-hosted backend uses libLLVM through the LLVM C API as the only
production object-emission path. Textual LLVM remains part of this checklist as
a debug, diagnostic, golden-test, and artifact-inspection output printed from the
in-memory module, never parsed as an input.

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/AbiLowering.cs` | `src/Compiler/AbiLowering.stark` | L | type/semantic facts | S05, S06, T09 |
| - [ ] | `src/Compiler/AbiLoweringHeuristics.cs` | `src/Compiler/AbiLoweringHeuristics.stark` | M | ABI lowering | S05 |
| - [ ] | `src/Compiler/LlvmIrEmitter.cs` | `src/Compiler/LlvmBackend.stark` | XL | ABI, direct LLVM module construction, libLLVM binding | L07, L13, S02, S05, S06, T08, T10 |
| - [ ] | libLLVM C API binding slice | `src/Compiler/LlvmNative.stark` or `stdlib/src/System/Llvm.stark` | L | FFI C strings, target/toolchain resolver | S09, S10, S11, S12, T08, T10 |
| - [ ] | libLLVM direct module/object emission and verifier driver | `src/Compiler/LlvmObjectEmitter.stark` | L | libLLVM binding, backend artifact model | T08, T10 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmAggregateEmissionSupport.cs` | `src/Compiler/LlvmIrEmission/LlvmAggregateEmissionSupport.stark` | M | type/ABI facts | S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmBuiltinAndHelperEmitter.cs` | `src/Compiler/LlvmIrEmission/LlvmBuiltinAndHelperEmitter.stark` | XL | text builder, target facts | S02, S05, T09, T10 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmDebugMetadataEmitter.cs` | `src/Compiler/LlvmIrEmission/LlvmDebugMetadataEmitter.stark` | L | LLVM metadata builder, alias facts | L13, S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmEmissionContext.cs` | `src/Compiler/LlvmIrEmission/LlvmEmissionContext.stark` | M | LLVM helpers | L13, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmEmissionContextBuilder.cs` | `src/Compiler/LlvmIrEmission/LlvmEmissionContextBuilder.stark` | M | emission context | L13, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmFunctionAttributeBuilder.cs` | `src/Compiler/LlvmIrEmission/LlvmFunctionAttributeBuilder.stark` | M | ABI/effect facts | L13, S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmFunctionBodyEmitter.cs` | `src/Compiler/LlvmIrEmission/LlvmFunctionBodyEmitter.stark` | XL | SSA, ABI, helpers | L13, S02, S05, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmFunctionSignatureBuilder.cs` | `src/Compiler/LlvmIrEmission/LlvmFunctionSignatureBuilder.stark` | L | ABI facts | S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmGlobalInitializerPlanner.cs` | `src/Compiler/LlvmIrEmission/LlvmGlobalInitializerPlanner.stark` | L | type/layout facts | S05, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmModuleSurfaceEmitter.cs` | `src/Compiler/LlvmIrEmission/LlvmModuleSurfaceEmitter.stark` | L | LLVM helpers | S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmSpecializationEmissionPlanner.cs` | `src/Compiler/LlvmIrEmission/LlvmSpecializationEmissionPlanner.stark` | L | specialization plan | S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmStringConstantEmission.cs` | `src/Compiler/LlvmIrEmission/LlvmStringConstantEmission.stark` | M | text literal helpers | S02, S03 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmTextOptimizationConstants.cs` | `src/Compiler/LlvmIrEmission/LlvmTextOptimizationConstants.stark` | S | none | S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmValueRangeFacts.cs` | `src/Compiler/LlvmIrEmission/LlvmValueRangeFacts.stark` | M | bounded integer/range facts | S05 |
| - [ ] | `src/Compiler/LlvmIrEmission/UnsupportedBodyEmissionException.cs` | `src/Compiler/LlvmIrEmission/UnsupportedBodyEmission.stark` | S | invariant policy | L01, L07 |

### LLVM Function Body Emitter Partials

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.AbiAndStorage.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/AbiAndStorage.stark` | L | function body emitter | L13, S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Aggregates.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/Aggregates.stark` | L | function body emitter | S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Analysis.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/Analysis.stark` | M | function body emitter | S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Assumptions.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/Assumptions.stark` | M | value/alias facts | L13, S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Calls.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/Calls.stark` | L | ABI/effect/call facts | L13, S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.ControlFlow.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/ControlFlow.stark` | L | SSA terminators | S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.DynamicStorageAndAddresses.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/DynamicStorageAndAddresses.stark` | L | dynamic storage/value facts | L13, S02, S06, S17 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Instructions.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/Instructions.stark` | L | SSA values | S02, S05, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.LocalsAndAlignment.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LocalsAndAlignment.stark` | L | ABI/layout facts | L13, S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Metadata.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/Metadata.stark` | M | debug/alias metadata | L13, S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.RawPointerLoops.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/RawPointerLoops.stark` | XL | loop/value/alias facts | L13, S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Utilities.cs` | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/Utilities.stark` | M | function body emitter | S02, S06 |

### Package Image

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/PackageImage/Models/PackageImageModels.cs` | `src/Compiler/PackageImage/Models/PackageImageModels.stark` | L | result/collections/text/package sections | L01, L11, S01, S02, S06, S14, T07 |
| - [ ] | `src/Compiler/PackageImage/Builder/PackageImageBuilder.cs` | `src/Compiler/PackageImage/Builder/PackageImageBuilder.stark` | L | package models, binary codec, inspection output | S02, S06, S14, T07 |
| - [ ] | `src/Compiler/PackageImage/Builder/SourceSurfaceSectionBuilder.cs` | `src/Compiler/PackageImage/Builder/SourceSurfaceSectionBuilder.stark` | L | syntax/type artifacts | S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Builder/TypedInterfaceSectionBuilder.cs` | `src/Compiler/PackageImage/Builder/TypedInterfaceSectionBuilder.stark` | L | type artifacts | S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Builder/CompilerFactsSectionBuilder.cs` | `src/Compiler/PackageImage/Builder/CompilerFactsSectionBuilder.stark` | L | semantic/ABI/layout facts | L13, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Builder/GenericTemplateSectionBuilder.cs` | `src/Compiler/PackageImage/Builder/GenericTemplateSectionBuilder.stark` | XL | typed template artifacts | L04, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Loader/PackageImageLoader.cs` | `src/Compiler/PackageImage/Loader/PackageImageLoader.stark` | L | package models, binary codec, compatibility diagnostics | L01, S01, S06, S14, T07 |
| - [ ] | `src/Compiler/PackageImage/Loader/TypedInterfaceSectionLoader.cs` | `src/Compiler/PackageImage/Loader/TypedInterfaceSectionLoader.stark` | L | type resolver/package models | S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Loader/CompilerFactsSectionLoader.cs` | `src/Compiler/PackageImage/Loader/CompilerFactsSectionLoader.stark` | L | compiler facts/package models | L13, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Loader/GenericTemplateSectionLoader.cs` | `src/Compiler/PackageImage/Loader/GenericTemplateSectionLoader.stark` | L | template models | S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Bridge/PackageImageSourceBridge.cs` | `src/Compiler/PackageImage/Bridge/PackageImageSourceBridge.stark` | XL | package models/parser/text builder | S02, S06, S14, T01 |
| - [ ] | `src/Compiler/PackageImage/Shared/GenericTemplatePublicationPolicy.cs` | `src/Compiler/PackageImage/Shared/GenericTemplatePublicationPolicy.stark` | M | package models | S06 |
| - [ ] | `src/Compiler/PackageImage/Shared/PackageEnumLayoutCodec.cs` | `src/Compiler/PackageImage/Shared/PackageEnumLayoutCodec.stark` | M | enum layout/type model | S05, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Shared/PackageTypeCodec.cs` | `src/Compiler/PackageImage/Shared/PackageTypeCodec.stark` | L | type model | L11, S06, S14 |

### Assembly and Small Fact Helpers

| Port | Source Path | Migration Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/StarkAsmArchitectureFacts.cs` | `src/Compiler/StarkAsmArchitectureFacts.stark` | S | none | S06 |
| - [ ] | `src/Compiler/StarkAsmRegisterFacts.cs` | `src/Compiler/StarkAsmRegisterFacts.stark` | S | set/string helpers | S06, S08 |

## Test Port Checklist

Each test target path is provisional and assumes the test harness lives under
`tests-stark/`.

### `tests/compiler.FeatureTests`

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [x] | `tests/compiler.FeatureTests/FeatureLlvmTestBase.cs` | `tests-stark/compiler.FeatureTests/FeatureTestSupport.stark` | M | shared harness module over `System.Testing.HostCompiler`: CompileLlvm/CompileTypeCheck/CompileFull/CompileMir, `*WithModule` temp-dir module resolution, loop-limit variant, diagnostic count/contains helpers | TEST-02, TEST-03, TEST-05, TEST-06, TEST-07 |
| - [x] | `tests/compiler.FeatureTests/BorrowingFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | S | facts live in the root file (runner collects root-file facts only) | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/EnumsFeatureTests.cs` | n/a | S | no facts to port (empty placeholder class in C#) | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/FloatingPointFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | S | ported | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/FunctionClassesFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | S | ported | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/FunctionKindsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | S | ported | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/GenericsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | M | ported; TypeCheckModel trigger/NamedTypes facts re-expressed behaviorally (mono symbols or type-check success) | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [x] | `tests/compiler.FeatureTests/IntegerFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | S | ported | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/ModulesAndImportsFeatureTests.cs` | n/a | M | no facts to port (empty placeholder class in C#) | TEST-02, TEST-04, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/StringsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | M | ported | TEST-02, TEST-03, TEST-06, S02 |
| - [x] | `tests/compiler.FeatureTests/StructRecordFeatureTests.cs` | n/a | S | no facts to port (empty placeholder class in C#) | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/StructsAndRecordsFeatureTests.cs` | n/a | S | no facts to port (empty placeholder class in C#) | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/ThreadSafetyLawFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | M | ported; ThreadSafetyLawFacts model assertions re-expressed as `where Transferable/Shareable` call-site diagnostics; InMemoryModuleResolver facts use the temp-dir `searchDirectories` mechanism | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/TraitsAndDoctrinesFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | M | ported; MIR object-graph facts re-expressed over the rendered `mir` artifact text; one-line regex matched via `System.Testing.AnyLineContainsBoth` | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/TraversalLoopFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | S | ported | TEST-02, TEST-03, TEST-06 |
| - [x] | `tests/compiler.FeatureTests/ComptimeFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FeatureTests.stark` | L | ported (143 facts, incl. the three MaximumCompileTimeLoopIterations facts via the loop-limit request option) | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [x] | `tests/compiler.FeatureTests/compiler.FeatureTests.csproj` | `tests-stark/compiler.FeatureTests/Stark.toml` | S | test project layout | T03, T12 |
| - [x] | `tests/compiler.FeatureTests/xunit.runner.json` | n/a | S | covered by the `stark test` generated runner; no per-project runner config needed | TEST-01, TEST-09, T12 |

### `tests/compiler.PipelineTests`

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineTestSupport.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineTestSupport.stark` | L | test harness, artifact API | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineSyntaxModelTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineSyntaxModelTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineLoadModulesTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineLoadModulesTests.stark` | M | pipeline support/temp files | TEST-02, TEST-04, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineFunctionEffectsTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineFunctionEffectsTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineTypeCheckTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineTypeCheckTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineInstantiationOwnershipTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineInstantiationOwnershipTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineMonomorphizationPlanTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineMonomorphizationPlanTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineEnumLayoutTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineEnumLayoutTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineSemanticValidateTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineSemanticValidateTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineSpecializationPlanTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineSpecializationPlanTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineSpecializationCodegenStrategyTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineSpecializationCodegenStrategyTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineLowerHirTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineLowerHirTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineLowerMirTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineLowerMirTests.stark` | L | pipeline support, MIR snapshot helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineLowerAbiTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineLowerAbiTests.stark` | M | pipeline support | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineOptimizeSsaTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineOptimizeSsaTests.stark` | XL | pipeline support, SSA snapshot helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineEmitLlvmTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineEmitLlvmTests.stark` | L | pipeline support, LLVM helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/CompilerPipelineFullIntegrationTests.cs` | `tests-stark/compiler.PipelineTests/CompilerPipelineFullIntegrationTests.stark` | L | all pipeline support | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.PipelineTests/compiler.PipelineTests.csproj` | `tests-stark/compiler.PipelineTests/Stark.toml` | S | test project layout | T03, T12 |
| - [ ] | `tests/compiler.PipelineTests/xunit.runner.json` | `tests-stark/compiler.PipelineTests/TestRunner.stark.toml` | S | generated test runner implementation | TEST-01, TEST-09, T12 |

### `tests/compiler.Tests`

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `tests/compiler.Tests/FallbackLogAssertions.cs` | `tests-stark/compiler.Tests/FallbackLogAssertions.stark` | M | diagnostic/log helpers | TEST-02, TEST-12 |
| - [~] | `tests/compiler.Tests/ParserSmokeTests.cs` | `tests-stark/compiler.Tests/ParserSmokeTests.stark` | S | parser/test harness; first valid/invalid smoke facts green under `stark test` in `CompilerTests.stark` | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/ParserConformanceTests.cs` | `tests-stark/compiler.Tests/ParserConformanceTests.stark` | M | parser/test harness | TEST-02, TEST-08, TEST-12 |
| - [ ] | `tests/compiler.Tests/ParserEdgeCaseTests.cs` | `tests-stark/compiler.Tests/ParserEdgeCaseTests.stark` | M | parser/test harness | TEST-02, TEST-08, TEST-12 |
| - [ ] | `tests/compiler.Tests/CommentTriviaTests.cs` | `tests-stark/compiler.Tests/CommentTriviaTests.stark` | S | parser/test harness | TEST-02, TEST-12 |
| - [ ] | `tests/compiler.Tests/DiagnosticRegressionTests.cs` | `tests-stark/compiler.Tests/DiagnosticRegressionTests.stark` | L | diagnostic helpers | TEST-02, TEST-03, TEST-12 |
| - [ ] | `tests/compiler.Tests/TypeCheckingTests.cs` | `tests-stark/compiler.Tests/TypeCheckingTests.stark` | XL | artifact API | TEST-02, TEST-03, TEST-06, TEST-07, TEST-12 |
| - [ ] | `tests/compiler.Tests/TypeTypingDiagnosticsTests.cs` | `tests-stark/compiler.Tests/TypeTypingDiagnosticsTests.stark` | L | artifact API | TEST-02, TEST-06, TEST-07, TEST-12 |
| - [ ] | `tests/compiler.Tests/TypeTypingExpressionFamilyTests.cs` | `tests-stark/compiler.Tests/TypeTypingExpressionFamilyTests.stark` | L | artifact API | TEST-02, TEST-08, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/SemanticValidationTests.cs` | `tests-stark/compiler.Tests/SemanticValidationTests.stark` | XL | artifact API | TEST-02, TEST-06, TEST-07, TEST-12 |
| - [ ] | `tests/compiler.Tests/FunctionSemanticsTests.cs` | `tests-stark/compiler.Tests/FunctionSemanticsTests.stark` | L | artifact API | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/V1LoweringContractTests.cs` | `tests-stark/compiler.Tests/V1LoweringContractTests.stark` | M | artifact API | TEST-02, TEST-06, TEST-07, TEST-12 |
| - [ ] | `tests/compiler.Tests/OwnershipValidationTests.cs` | `tests-stark/compiler.Tests/OwnershipValidationTests.stark` | XL | artifact API | TEST-02, TEST-06, TEST-07, TEST-12 |
| - [ ] | `tests/compiler.Tests/BorrowLivenessValidationTests.cs` | `tests-stark/compiler.Tests/BorrowLivenessValidationTests.stark` | L | artifact API | TEST-02, TEST-06, TEST-07, TEST-12 |
| - [ ] | `tests/compiler.Tests/OwnershipRoadmapRegressionTests.cs` | `tests-stark/compiler.Tests/OwnershipRoadmapRegressionTests.stark` | M | diagnostic helpers | TEST-02, TEST-12 |
| - [ ] | `tests/compiler.Tests/LoweringContractValidationTests.cs` | `tests-stark/compiler.Tests/LoweringContractValidationTests.stark` | L | artifact API | TEST-02, TEST-06, TEST-07, TEST-12 |
| - [ ] | `tests/compiler.Tests/MidLevelIrArtifactValidationTests.cs` | `tests-stark/compiler.Tests/MidLevelIrArtifactValidationTests.stark` | M | MIR artifact API | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.cs` | `tests-stark/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.stark` | L | MIR helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.Core.cs` | `tests-stark/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.Core.stark` | L | MIR helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.CompileTimeEvaluator.cs` | `tests-stark/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.CompileTimeEvaluator.stark` | M | MIR/CT helpers | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.LoweringInvariant.cs` | `tests-stark/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.LoweringInvariant.stark` | M | MIR helpers | TEST-02, TEST-12 |
| - [ ] | `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.PlaceLowerer.cs` | `tests-stark/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.PlaceLowerer.stark` | L | MIR helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.RuntimeDropLowerer.cs` | `tests-stark/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.RuntimeDropLowerer.stark` | M | MIR helpers | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.SwitchPatternLowerer.cs` | `tests-stark/compiler.Tests/MidLevelIrLowering/MidLevelIrLoweringTests.SwitchPatternLowerer.stark` | L | MIR helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/MidLevelIrLowering/MidLevelIrDynamicFixedArrayIndexingTests.cs` | `tests-stark/compiler.Tests/MidLevelIrLowering/MidLevelIrDynamicFixedArrayIndexingTests.stark` | M | MIR helpers | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/SsaLoweringTests.cs` | `tests-stark/compiler.Tests/SsaLoweringTests.stark` | L | SSA helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/SsaIrValidationTests.cs` | `tests-stark/compiler.Tests/SsaIrValidationTests.stark` | M | SSA helpers | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/SsaOptimizationTests.cs` | `tests-stark/compiler.Tests/SsaOptimizationTests.stark` | XL | SSA helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/SsaEmitterCoverageMatrixTests.cs` | `tests-stark/compiler.Tests/SsaEmitterCoverageMatrixTests.stark` | L | LLVM/SSA helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/LlvmIrEmissionTests.cs` | `tests-stark/compiler.Tests/LlvmIrEmissionTests.stark` | XL | LLVM helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/LlvmEmitterConversionTests.cs` | `tests-stark/compiler.Tests/LlvmEmitterConversionTests.stark` | L | LLVM helpers | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/LlvmTextOrderedComparisonEmissionTests.cs` | `tests-stark/compiler.Tests/LlvmTextOrderedComparisonEmissionTests.stark` | M | LLVM helpers | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.Tests/FixedArrayOrderedComparisonEmissionTests.cs` | `tests-stark/compiler.Tests/FixedArrayOrderedComparisonEmissionTests.stark` | M | LLVM helpers | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.Tests/PackageImageArchitectureTests.cs` | `tests-stark/compiler.Tests/PackageImageArchitectureTests.stark` | L | binary package/inspection helpers | TEST-02, TEST-03, TEST-07, TEST-11, S14, T07 |
| - [ ] | `tests/compiler.Tests/PackageImageCallableValueTests.cs` | `tests-stark/compiler.Tests/PackageImageCallableValueTests.stark` | M | binary package/inspection helpers | TEST-02, TEST-07, TEST-11, S14, T07 |
| - [ ] | `tests/compiler.Tests/PackageImageLoaderDiagnosticsTests.cs` | `tests-stark/compiler.Tests/PackageImageLoaderDiagnosticsTests.stark` | L | binary package/inspection helpers | TEST-02, TEST-03, TEST-11, TEST-12, S14, T07 |
| - [ ] | `tests/compiler.Tests/PackageImageTypedArrayInitializerTests.cs` | `tests-stark/compiler.Tests/PackageImageTypedArrayInitializerTests.stark` | M | binary package/inspection helpers | TEST-02, TEST-07, TEST-11, S14, T07 |
| - [ ] | `tests/compiler.Tests/GenericUseSiteInstantiationRegressionTests.cs` | `tests-stark/compiler.Tests/GenericUseSiteInstantiationRegressionTests.stark` | M | artifact helpers | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/LargeOwnedAggregateRuntimeTests.cs` | `tests-stark/compiler.Tests/LargeOwnedAggregateRuntimeTests.stark` | M | process/native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.Tests/StandardLibrarySourceTests.cs` | `tests-stark/compiler.Tests/StandardLibrarySourceTests.stark` | L | file/source helpers | TEST-02, TEST-04, TEST-06, S09, S10 |
| - [ ] | `tests/compiler.Tests/ExampleSourceTests.cs` | `tests-stark/compiler.Tests/ExampleSourceTests.stark` | M | file/process runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.Tests/BenchmarkSourceTests.cs` | `tests-stark/compiler.Tests/BenchmarkSourceTests.stark` | M | file/process runner | TEST-02, TEST-04, TEST-05, TEST-10 |
| - [ ] | `tests/compiler.Tests/BenchmarkRegressionScriptTests.cs` | `tests-stark/compiler.Tests/BenchmarkRegressionScriptTests.stark` | M | benchmark harness | TEST-05, TEST-10 |
| - [x] | `tests/compiler.Tests/compiler.Tests.csproj` | `tests-stark/compiler.Tests/Stark.toml` | S | test project layout | T03, T12 |
| - [ ] | `tests/compiler.Tests/xunit.runner.json` | `tests-stark/compiler.Tests/TestRunner.stark.toml` | S | generated test runner implementation | TEST-01, TEST-09, T12 |

### `tests/compiler.IntegrationTests`

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `tests/compiler.IntegrationTests/SerialToolchainCollection.cs` | `tests-stark/compiler.IntegrationTests/SerialToolchainCollection.stark` | S | platform/serial runner | TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/CompilerCliTests.cs` | `tests-stark/compiler.IntegrationTests/CompilerCliTests.stark` | L | process/temp runner | TEST-02, TEST-04, TEST-05, TEST-09, TEST-12 |
| - [ ] | `tests/compiler.IntegrationTests/ProjectCliTests.cs` | `tests-stark/compiler.IntegrationTests/ProjectCliTests.stark` | XL | process/temp/project harness | TEST-02, TEST-04, TEST-05, TEST-09, S13, T03 |
| - [ ] | `tests/compiler.IntegrationTests/CompilerPipelineIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/CompilerPipelineIntegrationTests.stark` | L | artifact/process runner | TEST-02, TEST-05, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.IntegrationTests/MultiFileIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/MultiFileIntegrationTests.stark` | L | temp file/project runner | TEST-02, TEST-04, TEST-05, TEST-06 |
| - [ ] | `tests/compiler.IntegrationTests/ExamplesCompileRunTests.cs` | `tests-stark/compiler.IntegrationTests/ExamplesCompileRunTests.stark` | L | process/native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/MidLevelIrRuntimeTests.cs` | `tests-stark/compiler.IntegrationTests/MidLevelIrRuntimeTests.stark` | L | native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/MidLevelIrDynamicFixedArrayIndexingRuntimeTests.cs` | `tests-stark/compiler.IntegrationTests/MidLevelIrDynamicFixedArrayIndexingRuntimeTests.stark` | M | native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/FixedArrayOrderedComparisonRuntimeTests.cs` | `tests-stark/compiler.IntegrationTests/FixedArrayOrderedComparisonRuntimeTests.stark` | M | native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/TextOrderedComparisonRuntimeTests.cs` | `tests-stark/compiler.IntegrationTests/TextOrderedComparisonRuntimeTests.stark` | M | native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/IntegerArithmeticFoldNativeCodegenTests.cs` | `tests-stark/compiler.IntegrationTests/IntegerArithmeticFoldNativeCodegenTests.stark` | M | native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/IntegerExponentRuntimeTests.cs` | `tests-stark/compiler.IntegrationTests/IntegerExponentRuntimeTests.stark` | M | native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/UnsignedIntegerRuntimeTests.cs` | `tests-stark/compiler.IntegrationTests/UnsignedIntegerRuntimeTests.stark` | M | native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.IntegrationTests/GenericUseSiteInstantiationIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/GenericUseSiteInstantiationIntegrationTests.stark` | M | native/artifact runner | TEST-02, TEST-04, TEST-05, TEST-07 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageCliToolingTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageCliToolingTests.stark` | L | package inspection/process/temp runner | TEST-02, TEST-04, TEST-05, TEST-11, S14, T07 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageOptimizationSummaryWrapperIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageOptimizationSummaryWrapperIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedArrayInitializerIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedArrayInitializerIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedAssignmentExpressionIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedAssignmentExpressionIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedComparisonChainIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedComparisonChainIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedDiscardedExpressionIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedDiscardedExpressionIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedGroupedLocalDeclarationIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedGroupedLocalDeclarationIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedNestedObjectCreationIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedNestedObjectCreationIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedObjectInitializerLocalDeclarationIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedObjectInitializerLocalDeclarationIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedPowerIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedPowerIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedRawPointerDereferenceIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedRawPointerDereferenceIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedSwitchPatternIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedSwitchPatternIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedTerminalIfIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedTerminalIfIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedTextFullViewIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedTextFullViewIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/PackageImageTypedUninitializedLocalDeclarationIntegrationTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageTypedUninitializedLocalDeclarationIntegrationTests.stark` | M | package runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
| - [ ] | `tests/compiler.IntegrationTests/compiler.IntegrationTests.csproj` | `tests-stark/compiler.IntegrationTests/Stark.toml` | S | test project layout | T03, T12 |
| - [ ] | `tests/compiler.IntegrationTests/xunit.runner.json` | `tests-stark/compiler.IntegrationTests/TestRunner.stark.toml` | S | generated test runner implementation | TEST-01, TEST-09, T12 |

### `tests/compiler.StandardLibraryTests`

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `tests/compiler.StandardLibraryTests/StandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/StandardLibraryTests.stark` | L | stdlib runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/StandardLibraryGenericTests.cs` | `tests-stark/compiler.StandardLibraryTests/StandardLibraryGenericTests.stark` | M | stdlib runner | TEST-02, TEST-05 |
| - [ ] | `tests/compiler.StandardLibraryTests/BookSampleStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/BookSampleStandardLibraryTests.stark` | M | source file runner | TEST-02, TEST-04, TEST-05 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemBackendBoundaryAuditTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemBackendBoundaryAuditTests.stark` | L | stdlib/source audit helpers | TEST-02, TEST-04, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemCollectionsStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemCollectionsStandardLibraryTests.stark` | L | stdlib runner | TEST-02, TEST-05, S06 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemConsoleStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemConsoleStandardLibraryTests.stark` | M | process runner | TEST-02, TEST-05 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemFileSystemStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemFileSystemStandardLibraryTests.stark` | L | temp/file runner | TEST-02, TEST-04, TEST-05, S10 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemIOFileStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemIOFileStandardLibraryTests.stark` | L | temp/file runner | TEST-02, TEST-04, TEST-05, S09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemIOPathStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemIOPathStandardLibraryTests.stark` | M | path test helpers | TEST-02, TEST-04, S11 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemMathStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemMathStandardLibraryTests.stark` | M | stdlib runner | TEST-02, TEST-05 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemMemoryContractAuditStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemMemoryContractAuditStandardLibraryTests.stark` | M | source/diagnostic helpers | TEST-02, TEST-12, S17 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemMemoryHelperStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemMemoryHelperStandardLibraryTests.stark` | M | stdlib runner | TEST-02, TEST-05, S17 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemMemoryStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemMemoryStandardLibraryTests.stark` | L | stdlib runner | TEST-02, TEST-05, S17 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemNetStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemNetStandardLibraryTests.stark` | M | platform/net gating | TEST-02, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemNetTcpStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemNetTcpStandardLibraryTests.stark` | L | TCP/platform runner | TEST-02, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemProcessStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemProcessStandardLibraryTests.stark` | M | process runner | TEST-02, TEST-05, S12 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemPromotedConsoleStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemPromotedConsoleStandardLibraryTests.stark` | M | stdlib runner | TEST-02, TEST-05 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemPromotedIOFileSystemStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemPromotedIOFileSystemStandardLibraryTests.stark` | L | temp/file runner | TEST-02, TEST-04, TEST-05, S09, S10 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemPromotedNetTcpStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemPromotedNetTcpStandardLibraryTests.stark` | L | TCP/platform runner | TEST-02, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemPromotedRuntimeBufferStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemPromotedRuntimeBufferStandardLibraryTests.stark` | M | stdlib runner | TEST-02, TEST-05 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemRangeNotationStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemRangeNotationStandardLibraryTests.stark` | M | stdlib runner | TEST-02, TEST-05 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemRawPointerAuditStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemRawPointerAuditStandardLibraryTests.stark` | M | source audit helpers | TEST-02, TEST-12 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemRuntimeBufferStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemRuntimeBufferStandardLibraryTests.stark` | M | stdlib runner | TEST-02, TEST-05 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemRuntimePlatformLinuxStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemRuntimePlatformLinuxStandardLibraryTests.stark` | M | platform gating | TEST-02, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemRuntimePlatformMacOSStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemRuntimePlatformMacOSStandardLibraryTests.stark` | M | platform gating | TEST-02, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemRuntimePlatformWindowsStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemRuntimePlatformWindowsStandardLibraryTests.stark` | M | platform gating | TEST-02, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemSyscallStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemSyscallStandardLibraryTests.stark` | M | platform gating | TEST-02, TEST-09 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemTestingStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemTestingStandardLibraryTests.stark` | M | test stdlib runner | TEST-01, TEST-02 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemTextStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemTextStandardLibraryTests.stark` | L | text helpers | TEST-02, TEST-05, S02, S03 |
| - [ ] | `tests/compiler.StandardLibraryTests/SystemThreadingStandardLibraryTests.cs` | `tests-stark/compiler.StandardLibraryTests/SystemThreadingStandardLibraryTests.stark` | M | platform/thread runner | TEST-02, TEST-05, TEST-09, S16 |
| - [ ] | `tests/compiler.StandardLibraryTests/compiler.StandardLibraryTests.csproj` | `tests-stark/compiler.StandardLibraryTests/Stark.toml` | S | test project layout | T03, T12 |
| - [ ] | `tests/compiler.StandardLibraryTests/xunit.runner.json` | `tests-stark/compiler.StandardLibraryTests/TestRunner.stark.toml` | S | generated test runner implementation | TEST-01, TEST-09, T12 |

## Dependency Order Summary

1. Test harness foundation: TEST-01 through TEST-12, plus S09-S12 for host
   compiler execution.
2. Handwritten parser implementation T01 and parser facade.
3. Core artifact/data model, diagnostics, generic collections, and typed
   compiler interners.
4. Type resolver, syntax model, type checking, semantic validation.
5. Lowering contract, ownership, MIR, borrow liveness.
6. SSA model, validation, optimization.
7. ABI and libLLVM emission, with textual LLVM retained for inspection artifacts.
8. Binary package image loading/building and JSON/text inspection output.
9. CLI/project/native tooling.
10. Integration/runtime/package tests.
