# Phase 5 - Port Checklist

Provisional target root: `src-stark/` for the compiler and `tests-stark/` for
tests. This is a planning convention only; final layout is decision OQ-01 in
`07-open-questions.md`.

Effort scale:

- S: small mechanical port or wrapper
- M: focused subsystem
- L: large subsystem/test family
- XL: very large or strategy-dependent subsystem

## Compiler Source Checklist

### Grammar and Parsing

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `Stark.g4` | `src-stark/Parsing/Stark.g4` | S | none | T01 |
| - [ ] | `src/Parsing/StarkLexer.cs` | `src-stark/Parsing/StarkLexer.stark` | XL | `Stark.g4` | T01, L12 |
| - [ ] | `src/Parsing/StarkParser.cs` | `src-stark/Parsing/StarkParser.stark` | XL | `Stark.g4` | T01, L12 |
| - [ ] | `src/Parsing/StarkVisitor.cs` | `src-stark/Parsing/StarkVisitor.stark` | L | `StarkParser` | T01, L12 |
| - [ ] | `src/Parsing/StarkBaseVisitor.cs` | `src-stark/Parsing/StarkBaseVisitor.stark` | M | `StarkVisitor` | T01, L12 |
| - [ ] | `src/Parsing/StarkSyntax.cs` | `src-stark/Parsing/StarkSyntax.stark` | M | lexer/parser strategy | T01, S02, S03, S18 |
| - [ ] | `src/Parsing/TextLiteralDecoder.cs` | `src-stark/Parsing/TextLiteralDecoder.stark` | M | `System.Text` builders/encoding | S02, S03, S05 |

### Entry Point, CLI, Project Driver, Native Toolchain

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Program.cs` | `src-stark/Program.stark` | S | `CompilerCli` | T02, T03, S12 |
| - [ ] | `src/Properties/AssemblyInfo.cs` | `src-stark/Build/AssemblyInfoReplacement.stark` | S | build metadata decision | T14 |
| - [ ] | `src/compiler.csproj` | `src-stark/Stark.toml` | M | build layout decision | T02, T03, T05, T11, OQ-01 |
| - [ ] | `src/Compiler/CompilerCli.cs` | `src-stark/Compiler/CompilerCli.stark` | XL | pipeline, native toolchain, package image | L01, L10, L11, S09, S10, S11, S12, S14, T03, T08, T15 |
| - [ ] | `src/Compiler/ProjectCliDriver.cs` | `src-stark/Compiler/ProjectCliDriver.stark` | XL | compiler CLI, native toolchain | L01, L10, L11, S10, S11, S12, S13, T03, T04, T05 |
| - [ ] | `src/Compiler/NativeToolchain.cs` | `src-stark/Compiler/NativeToolchain.stark` | L | process/path/file stdlib | L01, S09, S10, S11, S12, S15, T08, T09, T10 |
| - [ ] | `src/Compiler/LlvmTargetInfo.cs` | `src-stark/Compiler/LlvmTargetInfo.stark` | S | none | T09 |

### Core Artifacts, Diagnostics, Pipeline

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/CompilerArtifacts.cs` | `src-stark/Compiler/CompilerArtifacts.stark` | XL | collections/text/BigInt conventions | L01, L06, L11, L12, L13, S01, S02, S05, S06, S17 |
| - [ ] | `src/Compiler/CompilerDiagnostics.cs` | `src-stark/Compiler/CompilerDiagnostics.stark` | L | text builder/result conventions | L01, S01, S02, S15, TEST-12 |
| - [ ] | `src/Compiler/CompilerPipeline.cs` | `src-stark/Compiler/CompilerPipeline.stark` | L | artifacts/diagnostics | L01, L07, L11, S06, S15 |
| - [ ] | `src/Compiler/DefaultCompilerPipeline.cs` | `src-stark/Compiler/DefaultCompilerPipeline.stark` | XL | all pass constructors | L03, S06, T03, T05 |
| - [ ] | `src/Compiler/ArtifactTextRenderer.cs` | `src-stark/Compiler/ArtifactTextRenderer.stark` | M | artifacts/text builder | S02, S03, TEST-03 |

### Front-end Models, Modules, Names, Types

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/SyntaxModelFactory.cs` | `src-stark/Compiler/SyntaxModelFactory.stark` | L | parser facade, artifacts | L01, L04, L11, S06, T01 |
| - [ ] | `src/Compiler/DeclaredFunctionSyntax.cs` | `src-stark/Compiler/DeclaredFunctionSyntax.stark` | M | parser facade | T01, S06 |
| - [ ] | `src/Compiler/ModuleResolution.cs` | `src-stark/Compiler/ModuleResolution.stark` | L | file/path/package loading | S09, S10, S11, S14, T07, T11 |
| - [ ] | `src/Compiler/StarkTypeResolver.cs` | `src-stark/Compiler/StarkTypeResolver.stark` | L | artifacts, type aliases | L01, L06, L11, S05, S06 |
| - [ ] | `src/Compiler/FunctionOverloads.cs` | `src-stark/Compiler/FunctionOverloads.stark` | M | type model | L03, L06, S06 |
| - [ ] | `src/Compiler/FunctionGenericParameterFacts.cs` | `src-stark/Compiler/FunctionGenericParameterFacts.stark` | S | parser facade | S06 |
| - [ ] | `src/Compiler/GlobalSymbolNaming.cs` | `src-stark/Compiler/GlobalSymbolNaming.stark` | S | text builder | S02 |
| - [ ] | `src/Compiler/InterpolatedText.cs` | `src-stark/Compiler/InterpolatedText.stark` | M | text literal helpers | L08, S02, S03 |
| - [ ] | `src/Compiler/TypeCompatibilityFacts.cs` | `src-stark/Compiler/TypeCompatibilityFacts.stark` | M | type model | L06, L13, S06 |
| - [ ] | `src/Compiler/TextFormattingFacts.cs` | `src-stark/Compiler/TextFormattingFacts.stark` | S | text model | S02, S03 |

### Type Checking, Semantic Validation, Ownership

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/TypeChecking.cs` | `src-stark/Compiler/TypeChecking.stark` | XL | parser, artifacts, type resolver | L01, L02, L04, L06, L07, L11, L13, S01, S05, S06, S07 |
| - [ ] | `src/Compiler/SemanticValidation.cs` | `src-stark/Compiler/SemanticValidation.stark` | XL | type checking artifacts | L01, L02, L04, L06, L07, L11, L13, S05, S06 |
| - [ ] | `src/Compiler/OwnershipValidation.cs` | `src-stark/Compiler/OwnershipValidation.stark` | XL | type model, semantic facts | L01, L03, L07, L11, S06, S17 |
| - [ ] | `src/Compiler/NonLexicalBorrowLifetimeValidation.cs` | `src-stark/Compiler/NonLexicalBorrowLifetimeValidation.stark` | L | MIR, ownership model | L01, L03, S06 |
| - [ ] | `src/Compiler/LoweringContractValidation.cs` | `src-stark/Compiler/LoweringContractValidation.stark` | L | type/semantic facts | L01, L04, L07, L13, S06 |
| - [ ] | `src/Compiler/ParameterMemoryContractFacts.cs` | `src-stark/Compiler/ParameterMemoryContractFacts.stark` | M | type model | L13, S06 |
| - [ ] | `src/Compiler/IntegerRangeStorageFacts.cs` | `src-stark/Compiler/IntegerRangeStorageFacts.stark` | M | BigInt/range helpers | S05 |
| - [ ] | `src/Compiler/ConstProvenanceFacts.cs` | `src-stark/Compiler/ConstProvenanceFacts.stark` | S | type model | L13 |
| - [ ] | `src/Compiler/EnumLayoutBuilder.cs` | `src-stark/Compiler/EnumLayoutBuilder.stark` | M | type model, BigInt | S05, S06 |
| - [ ] | `src/Compiler/CompileTimeExpressionEvaluator.cs` | `src-stark/Compiler/CompileTimeExpressionEvaluator.stark` | L | parser, BigInt | L09, S05 |
| - [ ] | `src/Compiler/FunctionOptimizationSummaryBuilder.cs` | `src-stark/Compiler/FunctionOptimizationSummaryBuilder.stark` | M | parser/type facts | T01, S06 |
| - [ ] | `src/Compiler/GenericTemplateBodyComplexityEstimator.cs` | `src-stark/Compiler/GenericTemplateBodyComplexityEstimator.stark` | M | parser/template facts | T01, S06 |

### HIR/MIR Lowering

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.cs` | `src-stark/Compiler/MidLevelIrLowering/MidLevelIrLowering.stark` | XL | artifacts, type model | L01, L07, L11, S06, S17 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.cs` | `src-stark/Compiler/MidLevelIrLowering/FunctionMirBuilder.stark` | XL | MIR lowerer shell | L01, L02, L04, L07, L11, S06, S17 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.CompileTimeEvaluator.cs` | `src-stark/Compiler/MidLevelIrLowering/FunctionMirBuilder.CompileTimeEvaluator.stark` | L | function MIR builder, CT evaluator | L09, S05, S06 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.ImportedTemplateLowerer.cs` | `src-stark/Compiler/MidLevelIrLowering/FunctionMirBuilder.ImportedTemplateLowerer.stark` | XL | package image, template facts | L01, L04, S06, S14, T07 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.PlaceLowerer.cs` | `src-stark/Compiler/MidLevelIrLowering/FunctionMirBuilder.PlaceLowerer.stark` | XL | function MIR builder | L07, L13, S06 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.RuntimeDropLowerer.cs` | `src-stark/Compiler/MidLevelIrLowering/FunctionMirBuilder.RuntimeDropLowerer.stark` | L | ownership/type facts | L07, S17 |
| - [ ] | `src/Compiler/MidLevelIrLowering/MidLevelIrLowering.FunctionMirBuilder.SwitchPatternLowerer.cs` | `src-stark/Compiler/MidLevelIrLowering/FunctionMirBuilder.SwitchPatternLowerer.stark` | XL | switch/type facts | L04, S06 |

### SSA Lowering, Validation, Optimization

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/SsaLowering.cs` | `src-stark/Compiler/SsaLowering.stark` | XL | MIR artifacts | L07, L13, S06, S17 |
| - [ ] | `src/Compiler/SsaIrValidation.cs` | `src-stark/Compiler/SsaIrValidation.stark` | L | SSA artifacts | L07, L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization.cs` | `src-stark/Compiler/SsaOptimization.stark` | M | SSA optimizer files | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaAddressTakenFunctionPruner.cs` | `src-stark/Compiler/SsaOptimization/SsaAddressTakenFunctionPruner.stark` | M | SSA artifacts | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaAggregateConstructionStoreOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaAggregateConstructionStoreOptimizer.stark` | L | SSA artifacts | L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaAliasAwareMemoryOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaAliasAwareMemoryOptimizer.stark` | L | SSA value/effect facts | L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaAsciiToUnicodeLiteralSpecializer.cs` | `src-stark/Compiler/SsaOptimization/SsaAsciiToUnicodeLiteralSpecializer.stark` | L | SSA value facts, text helpers | S02, S03, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaCleanupOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaCleanupOptimizer.stark` | XL | SSA artifacts | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstGraphCallCseOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaConstGraphCallCseOptimizer.stark` | L | function effects, SSA | L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstLookupTableOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaConstLookupTableOptimizer.stark` | M | const initializer facts | S05, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstStdlibHelperSpecializer.cs` | `src-stark/Compiler/SsaOptimization/SsaConstStdlibHelperSpecializer.stark` | L | SSA value facts, path/text | S02, S11, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstantPropagator.cs` | `src-stark/Compiler/SsaOptimization/SsaConstantPropagator.stark` | L | SSA artifacts, BigInt | S05, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaConstantTextFormatSpecializer.cs` | `src-stark/Compiler/SsaOptimization/SsaConstantTextFormatSpecializer.stark` | L | text formatting/value facts | S02, S03, S05 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDirectCallDevirtualizer.cs` | `src-stark/Compiler/SsaOptimization/SsaDirectCallDevirtualizer.stark` | M | callable facts | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDirectCallInliner.cs` | `src-stark/Compiler/SsaOptimization/SsaDirectCallInliner.stark` | XL | function effects, SSA clone helpers | L03, S06, S17 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDynamicAppendLoopOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaDynamicAppendLoopOptimizer.stark` | L | value facts, dynamic storage | S06, S17 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDynamicStorageCallFactPolicy.cs` | `src-stark/Compiler/SsaOptimization/SsaDynamicStorageCallFactPolicy.stark` | S | dynamic storage facts | S17 |
| - [ ] | `src/Compiler/SsaOptimization/SsaDynamicStorageOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaDynamicStorageOptimizer.stark` | L | value/effect facts | S06, S17 |
| - [ ] | `src/Compiler/SsaOptimization/SsaFactDrivenBranchPruner.cs` | `src-stark/Compiler/SsaOptimization/SsaFactDrivenBranchPruner.stark` | M | value facts | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaIntegerArithmeticFolder.cs` | `src-stark/Compiler/SsaOptimization/SsaIntegerArithmeticFolder.stark` | L | BigInt/range facts | S05, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaOwnershipTrafficOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaOwnershipTrafficOptimizer.stark` | M | ownership/SSA | S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaScalarReplacementOptimizer.cs` | `src-stark/Compiler/SsaOptimization/SsaScalarReplacementOptimizer.stark` | L | alias-aware SSA | L13, S06 |
| - [ ] | `src/Compiler/SsaOptimization/SsaValueFactAnalyzer.cs` | `src-stark/Compiler/SsaOptimization/SsaValueFactAnalyzer.stark` | XL | BigInt, SSA facts | L13, S05, S06 |

### ABI and LLVM Emission

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/AbiLowering.cs` | `src-stark/Compiler/AbiLowering.stark` | L | type/semantic facts | S05, S06, T09 |
| - [ ] | `src/Compiler/AbiLoweringHeuristics.cs` | `src-stark/Compiler/AbiLoweringHeuristics.stark` | M | ABI lowering | S05 |
| - [ ] | `src/Compiler/LlvmIrEmitter.cs` | `src-stark/Compiler/LlvmIrEmitter.stark` | XL | ABI, LLVM emission modules | L07, L13, S02, S05, S06, T08, T10 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmAggregateEmissionSupport.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmAggregateEmissionSupport.stark` | M | type/ABI facts | S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmBuiltinAndHelperEmitter.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmBuiltinAndHelperEmitter.stark` | XL | text builder, target facts | S02, S05, T09, T10 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmDebugMetadataEmitter.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmDebugMetadataEmitter.stark` | L | LLVM text builder, alias facts | L13, S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmEmissionContext.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmEmissionContext.stark` | M | LLVM helpers | L13, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmEmissionContextBuilder.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmEmissionContextBuilder.stark` | M | emission context | L13, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmFunctionAttributeBuilder.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmFunctionAttributeBuilder.stark` | M | ABI/effect facts | L13, S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmFunctionBodyEmitter.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmFunctionBodyEmitter.stark` | XL | SSA, ABI, helpers | L13, S02, S05, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmFunctionSignatureBuilder.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmFunctionSignatureBuilder.stark` | L | ABI facts | S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmGlobalInitializerPlanner.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmGlobalInitializerPlanner.stark` | L | type/layout facts | S05, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmModuleSurfaceEmitter.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmModuleSurfaceEmitter.stark` | L | LLVM helpers | S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmSpecializationEmissionPlanner.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmSpecializationEmissionPlanner.stark` | L | specialization plan | S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmStringConstantEmission.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmStringConstantEmission.stark` | M | text literal helpers | S02, S03 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmTextOptimizationConstants.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmTextOptimizationConstants.stark` | S | none | S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/LlvmValueRangeFacts.cs` | `src-stark/Compiler/LlvmIrEmission/LlvmValueRangeFacts.stark` | M | BigInt/range facts | S05 |
| - [ ] | `src/Compiler/LlvmIrEmission/UnsupportedBodyEmissionException.cs` | `src-stark/Compiler/LlvmIrEmission/UnsupportedBodyEmission.stark` | S | invariant policy | L01, L07 |

### LLVM Function Body Emitter Partials

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.AbiAndStorage.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/AbiAndStorage.stark` | L | function body emitter | L13, S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Aggregates.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/Aggregates.stark` | L | function body emitter | S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Analysis.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/Analysis.stark` | M | function body emitter | S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Assumptions.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/Assumptions.stark` | M | value/alias facts | L13, S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Calls.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/Calls.stark` | L | ABI/effect/call facts | L13, S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.ControlFlow.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/ControlFlow.stark` | L | SSA terminators | S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.DynamicStorageAndAddresses.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/DynamicStorageAndAddresses.stark` | L | dynamic storage/value facts | L13, S02, S06, S17 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Instructions.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/Instructions.stark` | L | SSA values | S02, S05, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.LocalsAndAlignment.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/LocalsAndAlignment.stark` | L | ABI/layout facts | L13, S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Metadata.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/Metadata.stark` | M | debug/alias metadata | L13, S02 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.RawPointerLoops.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/RawPointerLoops.stark` | XL | loop/value/alias facts | L13, S02, S06 |
| - [ ] | `src/Compiler/LlvmIrEmission/FunctionBodyEmitter/LlvmFunctionBodyEmitter.Utilities.cs` | `src-stark/Compiler/LlvmIrEmission/FunctionBodyEmitter/Utilities.stark` | M | function body emitter | S02, S06 |

### Package Image

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/PackageImage/Models/PackageImageModels.cs` | `src-stark/Compiler/PackageImage/Models/PackageImageModels.stark` | L | result/collections/text | L01, L11, S01, S02, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Builder/PackageImageBuilder.cs` | `src-stark/Compiler/PackageImage/Builder/PackageImageBuilder.stark` | L | package models, JSON | S02, S06, S14, T07 |
| - [ ] | `src/Compiler/PackageImage/Builder/SourceSurfaceSectionBuilder.cs` | `src-stark/Compiler/PackageImage/Builder/SourceSurfaceSectionBuilder.stark` | L | syntax/type artifacts | S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Builder/TypedInterfaceSectionBuilder.cs` | `src-stark/Compiler/PackageImage/Builder/TypedInterfaceSectionBuilder.stark` | L | type artifacts | S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Builder/CompilerFactsSectionBuilder.cs` | `src-stark/Compiler/PackageImage/Builder/CompilerFactsSectionBuilder.stark` | L | semantic/ABI/layout facts | L13, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Builder/GenericTemplateSectionBuilder.cs` | `src-stark/Compiler/PackageImage/Builder/GenericTemplateSectionBuilder.stark` | XL | typed template artifacts | L04, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Loader/PackageImageLoader.cs` | `src-stark/Compiler/PackageImage/Loader/PackageImageLoader.stark` | L | package models, JSON | L01, S01, S06, S14, T07 |
| - [ ] | `src/Compiler/PackageImage/Loader/TypedInterfaceSectionLoader.cs` | `src-stark/Compiler/PackageImage/Loader/TypedInterfaceSectionLoader.stark` | L | type resolver/package models | S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Loader/CompilerFactsSectionLoader.cs` | `src-stark/Compiler/PackageImage/Loader/CompilerFactsSectionLoader.stark` | L | compiler facts/package models | L13, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Loader/GenericTemplateSectionLoader.cs` | `src-stark/Compiler/PackageImage/Loader/GenericTemplateSectionLoader.stark` | L | template models | S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Bridge/PackageImageSourceBridge.cs` | `src-stark/Compiler/PackageImage/Bridge/PackageImageSourceBridge.stark` | XL | package models/parser/text builder | S02, S06, S14, T01 |
| - [ ] | `src/Compiler/PackageImage/Shared/GenericTemplatePublicationPolicy.cs` | `src-stark/Compiler/PackageImage/Shared/GenericTemplatePublicationPolicy.stark` | M | package models | S06 |
| - [ ] | `src/Compiler/PackageImage/Shared/PackageEnumLayoutCodec.cs` | `src-stark/Compiler/PackageImage/Shared/PackageEnumLayoutCodec.stark` | M | enum layout/type model | S05, S06, S14 |
| - [ ] | `src/Compiler/PackageImage/Shared/PackageTypeCodec.cs` | `src-stark/Compiler/PackageImage/Shared/PackageTypeCodec.stark` | L | type model | L11, S06, S14 |

### Assembly and Small Fact Helpers

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `src/Compiler/StarkAsmArchitectureFacts.cs` | `src-stark/Compiler/StarkAsmArchitectureFacts.stark` | S | none | S06 |
| - [ ] | `src/Compiler/StarkAsmRegisterFacts.cs` | `src-stark/Compiler/StarkAsmRegisterFacts.stark` | S | set/string helpers | S06, S08 |

## Test Port Checklist

Each test target path is provisional and assumes the test harness lives under
`tests-stark/`.

### `tests/compiler.FeatureTests`

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `tests/compiler.FeatureTests/FeatureLlvmTestBase.cs` | `tests-stark/compiler.FeatureTests/FeatureLlvmTestBase.stark` | M | test harness, host compiler runner | TEST-02, TEST-03, TEST-05, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.FeatureTests/BorrowingFeatureTests.cs` | `tests-stark/compiler.FeatureTests/BorrowingFeatureTests.stark` | S | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/EnumsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/EnumsFeatureTests.stark` | M | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/FloatingPointFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FloatingPointFeatureTests.stark` | S | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/FunctionClassesFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FunctionClassesFeatureTests.stark` | S | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/FunctionKindsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/FunctionKindsFeatureTests.stark` | S | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/GenericsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/GenericsFeatureTests.stark` | M | `FeatureLlvmTestBase`, artifact access | TEST-02, TEST-03, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.FeatureTests/IntegerFeatureTests.cs` | `tests-stark/compiler.FeatureTests/IntegerFeatureTests.stark` | S | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/ModulesAndImportsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/ModulesAndImportsFeatureTests.stark` | M | host compiler runner/temp files | TEST-02, TEST-04, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/StringsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/StringsFeatureTests.stark` | M | text helpers | TEST-02, TEST-03, TEST-06, S02 |
| - [ ] | `tests/compiler.FeatureTests/StructRecordFeatureTests.cs` | `tests-stark/compiler.FeatureTests/StructRecordFeatureTests.stark` | S | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/StructsAndRecordsFeatureTests.cs` | `tests-stark/compiler.FeatureTests/StructsAndRecordsFeatureTests.stark` | S | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/TraitsAndDoctrinesFeatureTests.cs` | `tests-stark/compiler.FeatureTests/TraitsAndDoctrinesFeatureTests.stark` | M | `FeatureLlvmTestBase` | TEST-02, TEST-03, TEST-06 |
| - [ ] | `tests/compiler.FeatureTests/compiler.FeatureTests.csproj` | `tests-stark/compiler.FeatureTests/Stark.toml` | S | test project layout | T03, T12 |
| - [ ] | `tests/compiler.FeatureTests/xunit.runner.json` | `tests-stark/compiler.FeatureTests/TestRunner.stark.toml` | S | Stark test runner decision | TEST-01, TEST-09, T12 |

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
| - [ ] | `tests/compiler.PipelineTests/xunit.runner.json` | `tests-stark/compiler.PipelineTests/TestRunner.stark.toml` | S | Stark test runner decision | TEST-01, TEST-09, T12 |

### `tests/compiler.Tests`

| Port | Source Path | Provisional Target Path | Effort | Depends On Checklist Items | Gap Dependencies |
|---|---|---|---|---|---|
| - [ ] | `tests/compiler.Tests/FallbackLogAssertions.cs` | `tests-stark/compiler.Tests/FallbackLogAssertions.stark` | M | diagnostic/log helpers | TEST-02, TEST-12 |
| - [ ] | `tests/compiler.Tests/ParserSmokeTests.cs` | `tests-stark/compiler.Tests/ParserSmokeTests.stark` | S | parser/test harness | TEST-02, TEST-06, TEST-07 |
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
| - [ ] | `tests/compiler.Tests/PackageImageArchitectureTests.cs` | `tests-stark/compiler.Tests/PackageImageArchitectureTests.stark` | L | JSON/package helpers | TEST-02, TEST-03, TEST-07, TEST-11, S14 |
| - [ ] | `tests/compiler.Tests/PackageImageCallableValueTests.cs` | `tests-stark/compiler.Tests/PackageImageCallableValueTests.stark` | M | JSON/package helpers | TEST-02, TEST-07, TEST-11, S14 |
| - [ ] | `tests/compiler.Tests/PackageImageLoaderDiagnosticsTests.cs` | `tests-stark/compiler.Tests/PackageImageLoaderDiagnosticsTests.stark` | L | JSON/package helpers | TEST-02, TEST-03, TEST-11, TEST-12, S14 |
| - [ ] | `tests/compiler.Tests/PackageImageTypedArrayInitializerTests.cs` | `tests-stark/compiler.Tests/PackageImageTypedArrayInitializerTests.stark` | M | JSON/package helpers | TEST-02, TEST-07, TEST-11, S14 |
| - [ ] | `tests/compiler.Tests/GenericUseSiteInstantiationRegressionTests.cs` | `tests-stark/compiler.Tests/GenericUseSiteInstantiationRegressionTests.stark` | M | artifact helpers | TEST-02, TEST-06, TEST-07 |
| - [ ] | `tests/compiler.Tests/LargeOwnedAggregateRuntimeTests.cs` | `tests-stark/compiler.Tests/LargeOwnedAggregateRuntimeTests.stark` | M | process/native runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.Tests/StandardLibrarySourceTests.cs` | `tests-stark/compiler.Tests/StandardLibrarySourceTests.stark` | L | file/source helpers | TEST-02, TEST-04, TEST-06, S09, S10 |
| - [ ] | `tests/compiler.Tests/ExampleSourceTests.cs` | `tests-stark/compiler.Tests/ExampleSourceTests.stark` | M | file/process runner | TEST-02, TEST-04, TEST-05, TEST-09 |
| - [ ] | `tests/compiler.Tests/BenchmarkSourceTests.cs` | `tests-stark/compiler.Tests/BenchmarkSourceTests.stark` | M | file/process runner | TEST-02, TEST-04, TEST-05, TEST-10 |
| - [ ] | `tests/compiler.Tests/BenchmarkRegressionScriptTests.cs` | `tests-stark/compiler.Tests/BenchmarkRegressionScriptTests.stark` | M | benchmark harness | TEST-05, TEST-10 |
| - [ ] | `tests/compiler.Tests/compiler.Tests.csproj` | `tests-stark/compiler.Tests/Stark.toml` | S | test project layout | T03, T12 |
| - [ ] | `tests/compiler.Tests/xunit.runner.json` | `tests-stark/compiler.Tests/TestRunner.stark.toml` | S | Stark test runner decision | TEST-01, TEST-09, T12 |

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
| - [ ] | `tests/compiler.IntegrationTests/PackageImageCliToolingTests.cs` | `tests-stark/compiler.IntegrationTests/PackageImageCliToolingTests.stark` | L | JSON/process/temp runner | TEST-02, TEST-04, TEST-05, TEST-11, S14 |
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
| - [ ] | `tests/compiler.IntegrationTests/xunit.runner.json` | `tests-stark/compiler.IntegrationTests/TestRunner.stark.toml` | S | Stark test runner decision | TEST-01, TEST-09, T12 |

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
| - [ ] | `tests/compiler.StandardLibraryTests/xunit.runner.json` | `tests-stark/compiler.StandardLibraryTests/TestRunner.stark.toml` | S | Stark test runner decision | TEST-01, TEST-09, T12 |

## Dependency Order Summary

1. Test harness foundation: TEST-01 through TEST-12, plus S09-S12 for host
   compiler execution.
2. Parser strategy T01 and parser facade.
3. Core artifact/data model and diagnostics.
4. Type resolver, syntax model, type checking, semantic validation.
5. Lowering contract, ownership, MIR, borrow liveness.
6. SSA model, validation, optimization.
7. ABI and LLVM emission.
8. Package image loading/building.
9. CLI/project/native tooling.
10. Integration/runtime/package tests.
