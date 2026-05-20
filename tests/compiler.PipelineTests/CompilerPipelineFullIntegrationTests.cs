using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineFullIntegrationTests
{
    [Fact]
    public void MinimalModuleRunsThroughTheFullPipeline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);
        Assert.Equal("Demo", syntaxModel.ModuleName);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? semanticValidation));
        Assert.NotNull(semanticValidation);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? specializationPlan));
        Assert.NotNull(specializationPlan);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? specializationCodegenStrategy));
        Assert.NotNull(specializationCodegenStrategy);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OwnershipValidation, out OwnershipValidationModel? ownershipValidation));
        Assert.NotNull(ownershipValidation);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoweringContractValidation, out LoweringContractValidationModel? loweringContractValidation));
        Assert.NotNull(loweringContractValidation);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
        Assert.NotNull(abiModel);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("ModuleID = 'Demo'", llvmModule.Text);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssaModule));
        Assert.NotNull(ssaModule);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsaModule));
        Assert.NotNull(optimizedSsaModule);
        Assert.Equal(35, result.Executions.Count(static execution => execution.Status == PassExecutionStatus.Executed));
        Assert.Contains(
            result.Logs,
            log => log.Severity == DiagnosticSeverity.Info
                && log.Category == "pipeline"
                && log.EventId == "pass-completed"
                && log.Stage == "emit-llvm"
                && log.Verbosity == CompilerLogVerbosity.Verbose
                && log.Kind == CompilerLogKind.Pipeline
                && log.Outcome == CompilerLogOutcome.Continued
                && log.Data is not null
                && log.Data.TryGetValue("status", out var status)
                && string.Equals(status, PassExecutionStatus.Executed.ToString(), StringComparison.Ordinal));
    }


    [Fact]
    public void PipelinePreservesMirSwitchBreakShapeAndNormalizesOptimizedSsaControlFlow()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            fn i32[min max] Run(bool flag, i32[min max] limit) {
                stack mut i32[min max] sum = 0;
                stack mut i32[min max] i = 0;

                while willexit (i < limit) {
                    switch (flag) {
                        case true:
                            sum = sum + 1;
                            break;
                        case false:
                            break;
                    }

                    i = i + 1;
                }

                return sum;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var mirFunction = Assert.Single(mir.Functions, static function => function.Name == "Run");
        Assert.Contains(mirFunction.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Switch);
        Assert.Contains(mirFunction.Blocks, block => block.Label.Contains("switch_exit", StringComparison.Ordinal));

        var ssaFunction = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        Assert.Contains(ssaFunction.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Switch);
        var loopHeader = Assert.Single(ssaFunction.Blocks, block => block.Label.Contains("while_willexit_cond", StringComparison.Ordinal));
        Assert.DoesNotContain(
            loopHeader.Phis,
            static phi => phi.Incomings.Any(incoming => incoming.Value is SsaValueReference reference
                                                       && reference.Name == phi.ResultName));
    }


    [Fact]
    public void PipelineFoldsPureConstantsInMirAndOptimizedSsa()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            unsafe fn i32[min max] Run() {
                return (1 + 2) * 3;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var mirFunction = Assert.Single(mir.Functions, static function => function.Name == "Run");
        var mirBlock = Assert.Single(mirFunction.Blocks);
        Assert.Empty(mirBlock.Statements);
        var mirValue = Assert.IsType<MidLevelIrIntegerConstantOperand>(mirBlock.Terminator.Value);
        Assert.Equal(9, (int)mirValue.Value);

        var ssaFunction = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var ssaBlock = Assert.Single(ssaFunction.Blocks);
        Assert.Empty(ssaBlock.Instructions);
        var ssaValue = Assert.IsType<SsaIntegerConstant>(ssaBlock.Terminator.Value);
        Assert.Equal(9, (int)ssaValue.Value);
    }


    [Fact]
    public void PipelineCarriesSourceLocationsThroughMirAndSsaArtifacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            fn i32[min max] Run(i32[min max] input) {
                stack mut i32[min max] value = input;
                value = value + 1;
                return value;
            }
            """,
            "/virtual/Demo.stark"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var mirFunction = Assert.Single(mir.Functions, static function => function.Name == "Run");
        Assert.NotNull(mirFunction.Location);
        Assert.Equal("/virtual/Demo.stark", mirFunction.Location!.FilePath);
        Assert.Equal(3, mirFunction.Location.Line);
        Assert.Contains(
            mirFunction.Blocks.SelectMany(static block => block.Statements),
            statement => statement.Location is { FilePath: "/virtual/Demo.stark", Line: 4 });
        Assert.NotNull(mirFunction.Blocks[0].Terminator.Location);
        Assert.Equal("/virtual/Demo.stark", mirFunction.Blocks[0].Terminator.Location!.FilePath);

        var ssaFunction = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.NotNull(ssaFunction.Location);
        Assert.Equal("/virtual/Demo.stark", ssaFunction.Location!.FilePath);
        Assert.Equal(3, ssaFunction.Location.Line);
        Assert.Contains(
            ssaFunction.Blocks.SelectMany(static block => block.Instructions),
            instruction => instruction switch
            {
                SsaValueInstruction { Location: { FilePath: "/virtual/Demo.stark", Line: 4 or 5 } } => true,
                SsaAllocateLocalInstruction { Location: { FilePath: "/virtual/Demo.stark", Line: 4 } } => true,
                SsaStoreLocalInstruction { Location: { FilePath: "/virtual/Demo.stark", Line: 4 or 5 } } => true,
                _ => false
            });
        Assert.NotNull(ssaFunction.Blocks[0].Terminator.Location);

        var mirText = ArtifactTextRenderer.Render(mir);
        var ssaText = ArtifactTextRenderer.Render(ssa);
        Assert.Contains("location: /virtual/Demo.stark:3:", mirText);
        Assert.Contains("@ /virtual/Demo.stark:4:", mirText);
        Assert.Contains("location: /virtual/Demo.stark:3:", ssaText);
        Assert.Contains("@ /virtual/Demo.stark:", ssaText);
    }


    [Fact]
    public void OptimizationLevelZeroPreservesRawSsaBeforeLlvmEmission()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(bool flag) {
                    stack mut i32[min max] value = 0;
                    if (flag) {
                        value = 1;
                    } else {
                        value = 2;
                    }

                    return value;
                }
                """),
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        Assert.Equal(ArtifactTextRenderer.Render(ssa), ArtifactTextRenderer.Render(optimizedSsa));

        var function = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        Assert.Contains(function.Blocks, static block => block.Phis.Count != 0);
        Assert.Contains(function.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);
    }

    [Fact]
    public void DebugFriendlyOptimizationLevelPreservesRawSsaBeforeLlvmEmission()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(bool flag) {
                    stack mut i32[min max] value = 0;
                    if (flag) {
                        value = 1;
                    } else {
                        value = 2;
                    }

                    return value;
                }
                """),
            new CompilerOptions(OptimizationLevel: CompilerOptimizationLevel.Og));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        Assert.Equal(ArtifactTextRenderer.Render(ssa), ArtifactTextRenderer.Render(optimizedSsa));

        var function = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        Assert.Contains(function.Blocks, static block => block.Phis.Count != 0);
        Assert.Contains(function.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);
    }


    [Fact]
    public void PipelineFoldsImportedConstantLawCallsAcrossMirSsaAndLlvm()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Lib
                module Demo

                unsafe fn i32[min max] Run() {
                    return Lib.Adjust(4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Lib", "/virtual/Lib.stark", IsExternal: false),
                        """
                        module Lib

                        public finite law i32[min max] Adjust(i32[min max] value) {
                            stack mut i32[min max] current = value;
                            if (current < 10) {
                                current = current + 3;
                            }

                            return current;
                        }
                        """,
                        "/virtual/Lib.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);

        var mirFunction = Assert.Single(mir.Functions, static function => function.Name == "Run");
        var mirBlock = Assert.Single(mirFunction.Blocks);
        Assert.Empty(mirBlock.Statements);
        var mirValue = Assert.IsType<MidLevelIrIntegerConstantOperand>(mirBlock.Terminator.Value);
        Assert.Equal(7, (int)mirValue.Value);

        var ssaFunction = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var ssaBlock = Assert.Single(ssaFunction.Blocks);
        Assert.Empty(ssaBlock.Instructions);
        var ssaValue = Assert.IsType<SsaIntegerConstant>(ssaBlock.Terminator.Value);
        Assert.Equal(7, (int)ssaValue.Value);

        Assert.Contains("ret i32 7", llvmModule.Text);
        Assert.DoesNotContain("call fastcc i32 @Lib_Adjust", llvmModule.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i32 @Lib.Adjust", llvmModule.Text, StringComparison.Ordinal);
    }


    [Fact]
    public void PipelineDoesNotPrintInformationalLogsToConsoleErrorByDefault()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var originalError = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);

        try
        {
            var result = pipeline.Run(new CompilationInput(
                """
                module Demo
                """));

            Assert.True(result.Succeeded);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }


    [Fact]
    public void FunctionKindsAndModifiersDeriveExpectedEffectProfiles()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            public finite law i32[min max] Add(i32[min max] left, i32[min max] right);
            public strictfp finite law f32 Precise(f32 left, f32 right);
            export cold unsafe ffi fn void Accept(rawptr<i8[min max]> value);
            internal hot fn i32[min max] Warm(i32[min max] value);
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        var add = effectModel.Functions["Add"];
        Assert.True(add.IsPure);
        Assert.True(add.NoSync);
        Assert.True(add.NoFree);
        Assert.True(add.NoUnwind);
        Assert.True(add.WillReturn);
        Assert.True(add.MustProgress);
        Assert.True(add.UseFastCallingConvention);

        var precise = effectModel.Functions["Precise"];
        Assert.True(precise.IsStrictFp);

        var accept = effectModel.Functions["Accept"];
        Assert.False(accept.IsPure);
        Assert.False(accept.UseFastCallingConvention);
        Assert.True(accept.NoUnwind);
        Assert.True(accept.IsFfi);
        Assert.True(accept.IsCold);

        var warm = effectModel.Functions["Warm"];
        Assert.True(warm.IsHot);
        Assert.False(warm.IsCold);
    }


    [Fact]
    public void CrashedPassesProduceStructuredErrorLogs()
    {
        var pipeline = new CompilerPipelineBuilder()
            .Add(new ThrowingPass())
            .Build();

        var result = pipeline.Run(new CompilationInput("module Demo"));

        Assert.False(result.Succeeded);
        var log = Assert.Single(result.Logs, log => log.Category == "pipeline" && log.EventId == "pass-failed");
        Assert.Equal(DiagnosticSeverity.Error, log.Severity);
        Assert.Equal("throwing-pass", log.Stage);
        Assert.NotNull(log.Data);
        Assert.Equal("System.InvalidOperationException", log.Data["exceptionType"]);
        Assert.Equal("boom", log.Data["exceptionMessage"]);
    }


    [Fact]
    public void AsmDeclarationsSelectTheMatchingTargetAndPreserveSyntaxMetadata()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import System.Syscall
                module Demo

                unsafe fn i32[min max] Run() {
                    return 0;
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("System.Syscall", "/virtual/System/Syscall.stark", IsExternal: false),
                        """
                        module System.Syscall

                        public unsafe ffi asm(x86_64) fn i64[min max] Syscall3(i64[min max] number, i64[min max] arg1, i64[min max] arg2, i64[min max] arg3)
                            in("rax") number,
                            in("rdi") arg1,
                            in("rsi") arg2,
                            in("rdx") arg3,
                            out("rax") return,
                            clobber("rcx", "r11")
                        {
                            "syscall"
                        }

                        public unsafe ffi asm(aarch64) fn i64[min max] Syscall3(i64[min max] number, i64[min max] arg1, i64[min max] arg2, i64[min max] arg3)
                            in("x8") number,
                            in("x0") arg1,
                            in("x1") arg2,
                            in("x2") arg3,
                            out("x0") return,
                            clobber("x8")
                        {
                            "svc #0"
                        }
                        """,
                        "/virtual/System/Syscall.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
        Assert.NotNull(loadedModules);
        Assert.True(loadedModules.TryGet("System.Syscall", out var importedModule));
        Assert.NotNull(importedModule);

        var syscallFunctions = importedModule.SyntaxModel.Declarations
            .Where(static declaration => declaration.Name == "Syscall3" && declaration.Function is not null)
            .ToArray();
        Assert.Single(syscallFunctions);

        var asm = syscallFunctions[0].Function!.Asm;
        Assert.NotNull(asm);
        Assert.Equal(StarkAsmArchitecture.X86_64, asm!.Architecture);
        Assert.Equal("x86_64", asm.ArchitectureText);
        Assert.Equal("syscall", asm.TemplateText);
        Assert.Collection(
            asm.Inputs,
            input => { Assert.Equal("rax", input.RegisterName); Assert.Equal("number", input.ValueName); },
            input => { Assert.Equal("rdi", input.RegisterName); Assert.Equal("arg1", input.ValueName); },
            input => { Assert.Equal("rsi", input.RegisterName); Assert.Equal("arg2", input.ValueName); },
            input => { Assert.Equal("rdx", input.RegisterName); Assert.Equal("arg3", input.ValueName); });
        Assert.Collection(
            asm.Outputs,
            output =>
            {
                Assert.Equal("rax", output.RegisterName);
                Assert.Equal("return", output.ValueName);
                Assert.True(output.BindsReturnValue);
            });
        Assert.Equal(["rcx", "r11"], asm.Clobbers);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);
        Assert.True(effectModel.Functions["System.Syscall.Syscall3"].IsFfi);
    }


    [Fact]
    public void AsmDeclarationsReportMissingTargetMatchesAndRejectUnsupportedV1Shapes()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public asm(x86_64) fn i64[min max] MissingFfi(i64[min max] number)
                    out("rax") return
                {
                    "syscall"
                }

                public unsafe ffi asm(aarch64) fn i64[min max] NoMatch(i64[min max] number)
                    out("x0") return
                {
                    "svc #0"
                }
                """),
            new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2105" && diagnostic.Message.Contains("MissingFfi", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2102" && diagnostic.Message.Contains("NoMatch", StringComparison.Ordinal));
    }


    [Fact]
    public void AsmDeclarationsReportMultipleMatchingTargetSpecificDefinitions()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public unsafe ffi asm(x86_64) fn i64[min max] Syscall0()
                    out("rax") return
                {
                    "syscall"
                }

                public unsafe ffi asm(x86_64) fn i64[min max] Syscall0()
                    out("rax") return
                {
                    "syscall"
                }
                """),
            new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2103" && diagnostic.Message.Contains("Syscall0", StringComparison.Ordinal));
    }


    [Fact]
    public void AsmDeclarationsRejectInvalidRegistersAndOperandBindingConflicts()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public unsafe ffi asm(x86_64) fn i64[min max] Broken(i64[min max] number, out i64[min max] result)
                    in("x0") number,
                    in("rsi") result,
                    out("rax") result,
                    clobber("rax", "rax")
                {
                    "syscall"
                }
                """),
            new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2106" && diagnostic.Message.Contains("x0", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2107" && diagnostic.Message.Contains("out' or 'init' parameter", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2108" && diagnostic.Message.Contains("clobber register 'rax'", StringComparison.Ordinal));
    }


    [Fact]
    public void AsmDeclarationsFlowIntoConservativeEffectsAndAbiLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public unsafe ffi asm(x86_64) fn i64[min max] Syscall2(i64[min max] number, rawptr<i8[min max]> path)
                    in("rax") number,
                    in("rdi") path,
                    out("rax") return,
                    clobber("rcx", "r11")
                {
                    "syscall"
                }
                """),
            new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);
        var effects = effectModel.Functions["Syscall2"];
        Assert.Equal(StarkFunctionKind.Fn, effects.Kind);
        Assert.False(effects.IsPure);
        Assert.True(effects.NoUnwind);
        Assert.False(effects.WillReturn);
        Assert.False(effects.MustProgress);
        Assert.False(effects.UseFastCallingConvention);
        Assert.True(effects.IsFfi);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
        Assert.NotNull(abiModel);
        var abi = abiModel.Functions["Syscall2"];
        Assert.Equal("Syscall2", abi.SymbolName);
        Assert.True(abi.IsFfi);
        Assert.Equal(StarkTypeKind.Integer, abi.LlvmReturnType.Kind);
        Assert.Collection(
            abi.UserParameters,
            parameter =>
            {
                Assert.Equal("number", parameter.SourceName);
                Assert.Equal(StarkTypeKind.Integer, parameter.SourceType.Kind);
                Assert.Equal(AbiParameterKind.Direct, parameter.Kind);
            },
            parameter =>
            {
                Assert.Equal("path", parameter.SourceName);
                Assert.Equal(StarkTypeKind.RawPointer, parameter.SourceType.Kind);
                Assert.Equal(StarkTypeKind.RawPointer, parameter.LlvmType.Kind);
                Assert.Equal(AbiParameterKind.Direct, parameter.Kind);
            });
    }


    [Fact]
    public void ImportedAsmDeclarationsFlowThroughHirMirAndSsaAsExplicitBypassFunctions()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Syscall
                module Demo

                unsafe fn i64[min max] Run(rawptr<i8[min max]> path) {
                    return Syscall.Syscall2(2, path);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Syscall", "/virtual/Syscall.stark", IsExternal: false),
                        """
                        module Syscall

                        public unsafe ffi asm(x86_64) fn i64[min max] Syscall2(i64[min max] number, rawptr<i8[min max]> path)
                            in("rax") number,
                            in("rdi") path,
                            out("rax") return,
                            clobber("rcx", "r11")
                        {
                            "syscall"
                        }
                        """,
                        "/virtual/System/Syscall.stark"
                    )
                ])));

        Assert.True(result.Succeeded);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.NotNull(hir);
        Assert.Contains(
            hir.Functions,
            function => function.Name == "Syscall.Syscall2"
                && !function.HasBody
                && function.BodyLoweringKind == FunctionBodyLoweringKind.AsmBypass);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.Contains(
            mir.Functions,
            function => function.Name == "Syscall.Syscall2"
                && !function.HasBody
                && !function.SupportsDirectCodeGeneration
                && function.Blocks.Count == 0
                && function.BodyLoweringKind == FunctionBodyLoweringKind.AsmBypass);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        Assert.Contains(
            ssa.Functions,
            function => function.Name == "Syscall.Syscall2"
                && !function.HasBody
                && !function.SupportsDirectCodeGeneration
                && function.Blocks.Count == 0
                && function.BodyLoweringKind == FunctionBodyLoweringKind.AsmBypass);
    }


    [Fact]
    public void LargeAggregateAbiUsesIndirectByValueParametersAndSRetReturns()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            struct Big {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            fn Big Make() {
                return new Big() { A = 1, B = 2, C = 3 };
            }

            fn i64[min max] Read(Big value) {
                return value.A + value.C;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
        Assert.NotNull(abiModel);

        var make = abiModel.Functions["Make"];
        Assert.True(make.ReturnsIndirect);
        Assert.Equal(StarkTypeKind.Void, make.LlvmReturnType.Kind);
        Assert.Equal(AbiParameterKind.SRet, Assert.Single(make.Parameters).Kind);

        var read = abiModel.Functions["Read"];
        Assert.False(read.ReturnsIndirect);
        var valueParameter = Assert.Single(read.UserParameters);
        Assert.Equal(AbiParameterKind.IndirectIn, valueParameter.Kind);
        Assert.Equal(StarkTypeKind.RawPointer, valueParameter.LlvmType.Kind);
    }


    [Fact]
    public void PlainFnsRefineToStrongerEffectProfilesFromSemanticAnalysis()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            struct Box {
                i32[min max] Value;
            }

            fn i32[min max] Add(i32[min max] left, i32[min max] right) {
                return left + right;
            }

            fn i32[min max] Read(borrow Box box) {
                return box.Value;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        var add = effectModel.Functions["Add"];
        Assert.Equal(StarkFunctionKind.FiniteLaw, add.Kind);
        Assert.True(add.IsPure);
        Assert.True(add.WillReturn);
        Assert.True(add.MustProgress);
        Assert.False(add.ReadsArgumentMemory);

        var read = effectModel.Functions["Read"];
        Assert.Equal(StarkFunctionKind.FiniteLaw, read.Kind);
        Assert.True(read.IsPure);
        Assert.True(read.WillReturn);
        Assert.True(read.MustProgress);
        Assert.True(read.ReadsArgumentMemory);
    }


    [Fact]
    public void VoidCallsUsedAsValuesFailDuringTypeCheckingBeforeMirLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            fn void A() {
                return;
            }

            fn bool Run(bool flag) {
                return (flag ? A() : A()) == (flag ? A() : A());
                }
            """));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Stage == "type-check"
                && diagnostic.Message.Contains("Operator '==' cannot compare 'void' and 'void'.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Logs,
            log => log.Category == "lowering"
                && log.EventId == "unsupported-lowering"
                && log.Stage == "lower-mir");
    }


    [Fact]
    public void EmitLlvmModesReportTypeDiagnosticsBeforeLoweringForVoidCallsUsedAsValues()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn void A() {
                    return;
                }

                fn bool Run(bool flag) {
                    return (flag ? A() : A()) == (flag ? A() : A());
                }
                """),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Stage == "type-check"
                && diagnostic.Message.Contains("Operator '==' cannot compare 'void' and 'void'.", StringComparison.Ordinal));
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? _));
    }


    [Fact]
    public void NestedAggregateRuntimeDropsDoNotTriggerUnsupportedMirLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                static mut i32[min max] Counter = 0;

                fn void Bump(i32[min max] value) {
                    Counter = Counter + value;
                    return;
                }

                struct Resource {
                    i32[min max] Value;

                    drop {
                        Bump(self.Value);
                    }
                }

                enum Token {
                    End,
                    Text(Resource),
                }

                struct Holder {
                    Token Token;
                    Resource Backup;
                }

                export fn i32[min max] main() {
                    {
                        stack Holder holder = new Holder() {
                            Token = Token.Text(new Resource() { Value = 3 }),
                            Backup = new Resource() { Value = 4 }
                        };
                    }

                    return 0;
                }
                """),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            result.Logs,
            log => log.Category == "lowering"
                && log.EventId == "unsupported-lowering"
                && log.Stage == "lower-mir");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
    }

    [Fact]
    public void NestedInitializersEmitLlvmWithoutUnsupportedLoweringLogs()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Inner {
                    i32[min max][2] Pair;
                }

                struct Outer {
                    i32[min max] Score;
                    Inner Node;
                }

                fn i32[min max] MakeScore() {
                    return 9;
                }

                fn i32[min max] MakeLeft() {
                    return 4;
                }

                fn i32[min max] MakeRight() {
                    return 7;
                }

                export fn i32[min max] main() {
                    stack Outer outer = new Outer() {
                        Score = MakeScore(),
                        Node = { Pair = { MakeLeft(), MakeRight() } }
                    };
                    stack i32[min max][3] buffer = { 1, 2, 3 };
                    stack i32[min max] total = outer.Node.Pair[0] + outer.Node.Pair[1] + outer.Score + buffer[2];
                    return total;
                }
                """),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            result.Logs,
            log => log.Category == "lowering"
                && log.EventId == "unsupported-lowering"
                && log.Stage == "lower-mir");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
    }

    [Fact]
    public void ReadonlyScalarArrayGlobalsCanUseVectorizationFriendlyAlignment()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            const i32[min max][4] Lookup = { 1, 2, 3, 4 };
            static mut i32[min max][4] Scratch = { 5, 6, 7, 8 };

            fn i32[min max] Run(i32[min max] index) {
                return Lookup[index] + Scratch[index];
            }
            """));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("constant [4 x i32] [i32 1, i32 2, i32 3, i32 4], align 16", llvmModule.Text);
        Assert.Contains("@Scratch = global [4 x i32] [i32 5, i32 6, i32 7, i32 8], align 4", llvmModule.Text);
    }

    [Fact]
    public void SupportedComparisonFamiliesEmitLlvmWithoutUnsupportedLoweringLogs()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Many(i32[min max] A, i32[min max] B, i32[min max] C, i32[min max] D, i32[min max] E) { }
                record Label(ascii Tag, unicode Word) { }

                enum Token {
                    None,
                    Pair(i32[min max], i32[min max]),
                }

                export unsafe fn i32[min max] main() {
                    stack Many lessLeft = new Many(1, 2, 3, 4, 5);
                    stack Many lessRight = new Many(1, 2, 3, 4, 6);

                    stack i32[min max][3] sameLeft = { 1, 2, 3 };
                    stack i32[min max][3] sameRight = { 1, 2, 3 };
                    stack i32[min max][3] greaterLeft = { 1, 2, 4 };
                    stack i32[min max][3] greaterRight = { 1, 2, 3 };

                    stack i32[min max][3] leftValues = { 1, 2, 3 };
                    stack i32[min max][3] rightValues = { 1, 2, 3 };
                    stack i32[min max][] leftView = leftValues;
                    stack i32[min max][] rightView = rightValues;

                    if (lessLeft < lessRight
                        && sameLeft == sameRight
                        && greaterLeft > greaterRight
                        && "cab!"[1, 2] == "zab?"[1, 2]
                        && "cab!"[1, 2] < "cac?"[1, 2]
                        && ((unicode)"caf\u00E9!")[0, 4] != ((unicode)"cafe?")[0, 4]
                        && leftView != rightView
                        && new Label("cab!"[1, 2], ((unicode)"caf\u00E9!")[0, 4])
                            == new Label("zab?"[1, 2], ((unicode)"caf\u00E9?")[0, 4])
                        && Token.Pair(1, 2) > Token.Pair(1, 1)) {
                        return 7;
                    }

                    return 0;
                }
                """),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            result.Logs,
            log => log.Category == "lowering"
                && log.EventId == "unsupported-lowering"
                && log.Stage == "lower-mir");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
    }

    [Fact]
    public void ExpressionStatementsEmitLlvmWithoutUnsupportedLoweringLogs()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Box {
                    i32[min max] Value;
                }

                fn i32[min max] Next() {
                    return 7;
                }

                fn Box MakeBox(i32[min max] value) {
                    return new Box() { Value = value };
                }

                export fn i32[min max] main() {
                    stack mut Box box = new Box() { Value = 1 };
                    box.Value + 2;
                    MakeBox(Next());
                    new Box() { Value = Next() };
                    true ? MakeBox(3) : MakeBox(4);
                    return box.Value;
                }
                """),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            result.Logs,
            log => log.Category == "lowering"
                && log.EventId == "unsupported-lowering"
                && log.Stage == "lower-mir");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
    }

    [Fact]
    public void NestedPlaceUpdatesEmitLlvmWithoutUnsupportedLoweringLogs()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Cell {
                    i32[min max] Value;
                }

                struct Holder {
                    Cell[2] Cells;
                }

                export fn i32[min max] main() {
                    stack mut Holder holder = new Holder() {
                        Cells = { new Cell() { Value = 1 }, new Cell() { Value = 2 } }
                    };
                    stack i32[min max] index = 1;
                    holder.Cells[index].Value += 4;

                    stack mut mut i32[min max][3] values = { 1, 2, 3 };
                    stack mut mut i32[min max][] view = values;
                    view[0] = holder.Cells[index].Value;

                    return holder.Cells[index].Value + view[0];
                }
                """),
            new CompilerOptions(
                EmitLlvmIr: true,
                TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            result.Logs,
            log => log.Category == "lowering"
                && log.EventId == "unsupported-lowering"
                && log.Stage == "lower-mir");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
    }


    [Fact]
    public void HeapAllocatorLoweringAvoidsLlvmFallbackLogs()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            struct Box {
                i32[min max] Value;
            }

            unsafe fn i32[min max] Run() {
                heap Box box = new Box() { Value = 7 };
                return box.Value;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(result.Logs, log =>
            log.Category == "codegen"
            && log.EventId == "llvm-body-fallback"
            && log.Stage == "emit-llvm"
            && log.SymbolName == "Run");
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("define internal dso_local noalias nonnull noundef ptr @__stark_heap_alloc(i64 noundef %size, i64 noundef allocalign %alignment)", llvmModule.Text);
        Assert.Contains("call noalias nonnull noundef align 4 dereferenceable(4) ptr @__stark_heap_alloc(i64 noundef", llvmModule.Text);
        Assert.Contains("call void @__stark_heap_free(ptr %slot_box)", llvmModule.Text);
    }


    [Fact]
    public void ClosedWorldModulePrivateLawHelpersInferAlwaysInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            fn i32[min max] Add(i32[min max] left, i32[min max] right) {
                return left + right;
            }

            law i32[min max] Use() {
                return Add(1, 2);
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        var add = effectModel.Functions["Add"];
        Assert.Equal(StarkFunctionKind.FiniteLaw, add.Kind);
        Assert.Equal(InlinePreference.Inline, add.InlinePreference);

        var use = effectModel.Functions["Use"];
        Assert.Equal(InlinePreference.Inline, use.InlinePreference);
    }


    [Fact]
    public void ClosedWorldLawInliningRespectsExplicitHintsAndSkipsRecursiveHelpers()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            inlinehint fn i32[min max] Hint(i32[min max] value) {
                return value + 1;
            }

            noinline fn i32[min max] Stop(i32[min max] value) {
                return value + 1;
            }

            fn i32[min max] Loop(i32[min max] value) {
                if (value == 0) {
                    return 0;
                }

                return Loop(value - 1);
            }

            law i32[min max] Use(i32[min max] value) {
                return Hint(Stop(Loop(value)));
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        Assert.Equal(InlinePreference.InlineHint, effectModel.Functions["Hint"].InlinePreference);
        Assert.Equal(InlinePreference.NoInline, effectModel.Functions["Stop"].InlinePreference);
        Assert.Equal(InlinePreference.InlineHint, effectModel.Functions["Loop"].InlinePreference);
    }


    [Fact]
    public void ClosedWorldImportedModulePrivateLawHelpersInferAlwaysInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                unsafe fn i32[min max] Run() {
                    return Math.UseLaw();
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        law i32[min max] LawOnly() {
                            return 1;
                        }

                        law i32[min max] LawBlocked() {
                            return 2;
                        }

                        public law i32[min max] UseLaw() {
                            return LawOnly();
                        }

                        public fn i32[min max] UseFn() {
                            return LawBlocked();
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        Assert.Equal(InlinePreference.Inline, effectModel.Functions["Math.LawOnly"].InlinePreference);
        Assert.Equal(InlinePreference.InlineHint, effectModel.Functions["Math.LawBlocked"].InlinePreference);
        Assert.Equal(InlinePreference.Inline, effectModel.Functions["Math.UseLaw"].InlinePreference);
    }


    [Fact]
    public void ClosedWorldRootLawCallersCanInlineImportedNonExportLawChains()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                law i32[min max] Run() {
                    return Math.UseLaw();
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        law i32[min max] LawOnly() {
                            return 1;
                        }

                        public law i32[min max] UseLaw() {
                            return LawOnly();
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        Assert.Equal(InlinePreference.Inline, effectModel.Functions["Math.UseLaw"].InlinePreference);
        Assert.Equal(InlinePreference.Inline, effectModel.Functions["Math.LawOnly"].InlinePreference);
    }


    [Fact]
    public void ImportedLawEntrypointsWithMixedLawAndNonLawCallersStayInlineHintGlobally()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                law i32[min max] LawRun() {
                    return Math.UseLaw();
                }

                fn i32[min max] FnRun() {
                    unsafe {
                        Touch();
                    }

                    return Math.UseLaw();
                }

                unsafe ffi fn void Touch();
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public law i32[min max] UseLaw() {
                            return 1;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);

        Assert.Equal(InlinePreference.InlineHint, effectModel.Functions["Math.UseLaw"].InlinePreference);
    }


    [Fact]
    public void ImportedModulePrivateLawHelpersFlowIntoHirMirAndSsa()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                law i32[min max] Run() {
                    return Math.UseLaw();
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        law i32[min max] LawOnly() {
                            return 1;
                        }

                        public law i32[min max] UseLaw() {
                            return LawOnly();
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.NotNull(hir);
        Assert.Contains(hir.Functions, function => function.Name == "Math.LawOnly" && function.HasBody);
        Assert.Contains(hir.Functions, function => function.Name == "Math.UseLaw" && function.HasBody);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.Contains(mir.Functions, function => function.Name == "Math.LawOnly" && function.HasBody && function.Blocks.Count != 0);
        Assert.Contains(mir.Functions, function => function.Name == "Math.UseLaw" && function.HasBody && function.Blocks.Count != 0);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        Assert.Contains(ssa.Functions, function => function.Name == "Math.LawOnly" && function.HasBody && function.Blocks.Count != 0);
        Assert.Contains(ssa.Functions, function => function.Name == "Math.UseLaw" && function.HasBody && function.Blocks.Count != 0);
    }


    [Fact]
    public void ImportedModulesResolveThroughTheConfiguredResolver()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Core.Text
                module Demo

                public fn void Run() { return; }
                """),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    new ResolvedModuleReference("Core.Text", "/virtual/Core/Text.stark", IsExternal: false)
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.HasModule("Core.Text"));
        Assert.Single(moduleGraph.Imports);
        Assert.True(moduleGraph.Imports[0].IsResolved);
        Assert.False(moduleGraph.Imports[0].IsExported);
    }


    [Fact]
    public void ImportedFunctionsFromLoadedModulesParticipateInTypingAndEffects()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                unsafe fn i32[min max] Run() {
                    return Math.Add(3, 4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.Functions.ContainsKey("Math.Add"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);
        Assert.True(effectModel.Functions["Math.Add"].WillReturn);
        Assert.True(effectModel.Functions["Math.Add"].IsPure);
    }


    [Fact]
    public void ImportedSourceModulesFlowIntoHirMirAndSsaArtifacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                unsafe fn i32[min max] Run() {
                    return Math.Add(3, 4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.NotNull(hir);
        Assert.Contains(hir.Functions, function => function.Name == "Math.Add" && function.HasBody);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.Contains(mir.Functions, function => function.Name == "Math.Add" && function.HasBody && function.Blocks.Count != 0);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        Assert.Contains(ssa.Functions, function => function.Name == "Math.Add" && function.HasBody && function.Blocks.Count != 0);
    }


    [Fact]
    public void ImportedSourceModulesWithPrivateHelpersAndStringLiteralsLowerIntoMirAndSsa()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Path
                module Demo

                fn ascii Run() {
                    return Path.DirectorySeparator();
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Path", "/virtual/Path.stark", IsExternal: false),
                        """
                        module Path

                        unsafe ffi fn i32[min max] fputs(ascii text, rawptr<i8[min max]> stream);
                        const rawptr<i8[min max]> stdout = null;

                        public fn ascii DirectorySeparator() {
                            return "/";
                        }

                        public fn void Write(ascii text) {
                            fputs(text, stdout);
                            return;
                        }
                        """,
                        "/virtual/Path.stark"
                    )
                ])));

        Assert.True(result.Succeeded);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.NotNull(hir);
        Assert.Contains(hir.Functions, function => function.Name == "Path.DirectorySeparator" && function.HasBody);
        Assert.Contains(hir.Functions, function => function.Name == "Path.Write" && function.HasBody);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.Contains(mir.Functions, function => function.Name == "Path.DirectorySeparator" && function.HasBody && function.Blocks.Count != 0);
        Assert.Contains(mir.Functions, function => function.Name == "Path.Write" && function.HasBody && function.Blocks.Count != 0);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        Assert.Contains(ssa.Functions, function => function.Name == "Path.DirectorySeparator" && function.HasBody && function.Blocks.Count != 0);
        Assert.Contains(ssa.Functions, function => function.Name == "Path.Write" && function.HasBody && function.Blocks.Count != 0);
    }


    [Fact]
    public void DoctrineDeclarationsFlowIntoSyntaxTypeAndSemanticModels()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Laws

            public doctrine Numbers {
                finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                    return left + right;
                }
            }

            unsafe fn i32[min max] Run() {
                return Numbers.Add(1, 2);
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);
        Assert.Contains(syntaxModel.Declarations, declaration => declaration.Kind == DeclarationKind.Doctrine && declaration.Name == "Numbers");
        Assert.Contains(syntaxModel.Declarations, declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Numbers.Add");

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.TryGetValue("Numbers", out var doctrineType));
        Assert.NotNull(doctrineType);
        Assert.Equal(DeclarationKind.Doctrine, doctrineType.Kind);
        Assert.True(typeCheckModel.Functions.ContainsKey("Numbers.Add"));

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? semanticValidation));
        Assert.NotNull(semanticValidation);
        Assert.Contains("Numbers.Add", semanticValidation.Functions["Run"].CalledFunctions);
    }


    [Fact]
    public void TraitDeclarationsFlowIntoSyntaxTypeAndEffectModels()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Contracts

            public trait Comparable {
                law i32[min max] Compare(i32[min max] other);
            }

            unsafe fn i32[min max] Run() {
                return 0;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);
        Assert.Contains(syntaxModel.Declarations, declaration => declaration.Kind == DeclarationKind.Trait && declaration.Name == "Comparable");
        Assert.Contains(syntaxModel.Declarations, declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Comparable.Compare");

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.TryGetValue("Comparable", out var traitType));
        Assert.NotNull(traitType);
        Assert.Equal(DeclarationKind.Trait, traitType.Kind);
        Assert.True(typeCheckModel.Functions.ContainsKey("Comparable.Compare"));

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);
        Assert.True(effectModel.Functions["Comparable.Compare"].IsPure);
        Assert.False(effectModel.Functions["Comparable.Compare"].WillReturn);
    }


    [Fact]
    public void UnusedTypeAliasesDoNotBlockTheCurrentPipeline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            alias Byte = i8[min max];

            unsafe fn i32[min max] Run() {
                return 0;
            }
            """));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("define fastcc noundef i32 @Run()", llvmModule.Text);
    }


    [Fact]
    public void ImportedDoctrineMembersFromLoadedModulesParticipateInTypingAndEffects()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                unsafe fn i32[min max] Run() {
                    return Math.Numbers.Add(3, 4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public doctrine Numbers {
                            finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                                return left + right;
                            }
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Math.Numbers"));
        Assert.True(typeCheckModel.Functions.ContainsKey("Math.Numbers.Add"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);
        Assert.True(effectModel.Functions["Math.Numbers.Add"].WillReturn);
        Assert.True(effectModel.Functions["Math.Numbers.Add"].IsPure);
    }


    [Fact]
    public void ImportedTraitMembersFromLoadedModulesParticipateInTypingAndEffects()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                unsafe fn i32[min max] Run() {
                    return 0;
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public trait Comparable {
                            law i32[min max] Compare(i32[min max] other);
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Math.Comparable"));
        Assert.True(typeCheckModel.Functions.ContainsKey("Math.Comparable.Compare"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
        Assert.NotNull(effectModel);
        Assert.True(effectModel.Functions["Math.Comparable.Compare"].IsPure);
    }


    [Fact]
    public void ClosedWorldOptimizationModelCapturesTraitAndDoctrineRules()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                public doctrine Numbers {
                    finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                        return left + right;
                    }
                }

                public trait Comparable {
                    law i32[min max] Compare(i32[min max] other);
                }

                law i32[min max] UseLaw() {
                    return Math.Numbers.Add(1, 2);
                }

                fn i32[min max] UseFn() {
                    unsafe {
                        Touch();
                    }

                    return Math.Numbers.Add(3, 4);
                }

                unsafe ffi fn void Touch();
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public doctrine Numbers {
                            finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                                return left + right;
                            }
                        }

                        public trait Comparable {
                            law i32[min max] Compare(i32[min max] other);
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ClosedWorldOptimization, out ClosedWorldOptimizationModel? closedWorld));
        Assert.NotNull(closedWorld);

        Assert.Equal(ClosedWorldSealKind.SealedByDefault, closedWorld.Types["Numbers"].Seal);
        Assert.False(closedWorld.Types["Numbers"].HasRuntimeDispatch);
        Assert.Equal(ClosedWorldSealKind.SealedByDefault, closedWorld.Types["Comparable"].Seal);
        Assert.False(closedWorld.Types["Comparable"].HasRuntimeDispatch);
        Assert.Equal(ClosedWorldSealKind.SealedByDefault, closedWorld.Types["Math.Numbers"].Seal);
        Assert.Equal(ClosedWorldSealKind.SealedByDefault, closedWorld.Types["Math.Comparable"].Seal);

        Assert.Equal(
            new[] { ClosedWorldCallLoweringStrategy.DirectSharedBody },
            closedWorld.Functions["Numbers.Add"].SelectionOrder);
        Assert.Equal(ClosedWorldCodeGenerationMode.SharedCode, closedWorld.Functions["Numbers.Add"].CodeGenerationMode);
        Assert.True(closedWorld.Functions["Numbers.Add"].CanDevirtualize);

        Assert.Equal(
            new[] { ClosedWorldCallLoweringStrategy.CompileTimeOnlyContract },
            closedWorld.Functions["Comparable.Compare"].SelectionOrder);
        Assert.Equal(ClosedWorldCodeGenerationMode.MonomorphizationDeferred, closedWorld.Functions["Comparable.Compare"].CodeGenerationMode);
        Assert.False(closedWorld.Functions["Comparable.Compare"].CanDevirtualize);

        Assert.Equal(
            new[]
            {
                ClosedWorldCallLoweringStrategy.LawCallerSpecializedClone,
                ClosedWorldCallLoweringStrategy.DirectAbiBoundary
            },
            closedWorld.Functions["Math.Numbers.Add"].SelectionOrder);
        Assert.Equal(ClosedWorldCodeGenerationMode.CallerSpecializedClone, closedWorld.Functions["Math.Numbers.Add"].CodeGenerationMode);
        Assert.True(closedWorld.Functions["Math.Numbers.Add"].CanDevirtualize);

        Assert.Equal(
            new[] { ClosedWorldCallLoweringStrategy.CompileTimeOnlyContract },
            closedWorld.Functions["Math.Comparable.Compare"].SelectionOrder);
        Assert.Equal(ClosedWorldCodeGenerationMode.MonomorphizationDeferred, closedWorld.Functions["Math.Comparable.Compare"].CodeGenerationMode);
        Assert.False(closedWorld.Functions["Math.Comparable.Compare"].CanDevirtualize);
    }


    [Fact]
    public void ClosedWorldOptimizationModelCapturesImportedTopLevelLawRules()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                law i32[min max] UseLawClone(i32[min max] left, i32[min max] right) {
                    return Math.Add(left, right);
                }

                fn i32[min max] UseDirect(i32[min max] left, i32[min max] right) {
                    unsafe {
                        Touch();
                    }

                    return Math.Add(left, right);
                }

                unsafe ffi fn void Touch();
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ClosedWorldOptimization, out ClosedWorldOptimizationModel? closedWorld));
        Assert.NotNull(closedWorld);

        Assert.Equal(
            new[]
            {
                ClosedWorldCallLoweringStrategy.LawCallerSpecializedClone,
                ClosedWorldCallLoweringStrategy.DirectAbiBoundary
            },
            closedWorld.Functions["Math.Add"].SelectionOrder);
        Assert.Equal(ClosedWorldCodeGenerationMode.CallerSpecializedClone, closedWorld.Functions["Math.Add"].CodeGenerationMode);
        Assert.True(closedWorld.Functions["Math.Add"].CanDevirtualize);
    }


    [Fact]
    public void ClosedWorldOptimizationModelKeepsOpaqueImportedLawsAtAbiBoundary()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Math
                module Demo

                law i32[min max] UseLaw(i32[min max] left, i32[min max] right) {
                    return Math.Add(left, right);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        [Backend(Opaque)]
                        public finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ClosedWorldOptimization, out ClosedWorldOptimizationModel? closedWorld));
        Assert.NotNull(closedWorld);

        Assert.Equal(
            new[] { ClosedWorldCallLoweringStrategy.DirectAbiBoundary },
            closedWorld.Functions["Math.Add"].SelectionOrder);
        Assert.Equal(ClosedWorldCodeGenerationMode.SharedCode, closedWorld.Functions["Math.Add"].CodeGenerationMode);
        Assert.True(closedWorld.Functions["Math.Add"].CanDevirtualize);
    }


    [Fact]
    public void PrivateTransitiveImportsDoNotBecomeVisibleToTheRootModule()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                unsafe fn i32[min max] Run() {
                    return Math.Add(3, 4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Facade", "/virtual/Facade.stark", IsExternal: false),
                        """
                        import Math
                        module Facade

                        public fn i32[min max] Double(i32[min max] value) {
                            return Math.Add(value, value);
                        }
                        """,
                        "/virtual/Facade.stark"
                    ),
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK3003" && diagnostic.Message.Contains("Math", StringComparison.Ordinal));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.False(moduleGraph.HasModule("Math"));
        Assert.True(moduleGraph.ContainsLoadedModule("Math"));
    }


    [Fact]
    public void PublicReExportsMakeTransitiveModulesVisibleToTheRootModule()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                unsafe fn i32[min max] Run() {
                    return Math.Add(3, 4);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Facade", "/virtual/Facade.stark", IsExternal: false),
                        """
                        export import Math
                        module Facade

                        public fn i32[min max] Double(i32[min max] value) {
                            return Math.Add(value, value);
                        }
                        """,
                        "/virtual/Facade.stark"
                    ),
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                            return left + right;
                        }
                        """,
                        "/virtual/Math.stark"
                    )
                ])));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.HasModule("Math"));
        Assert.Contains(
            moduleGraph.Imports,
                    edge => edge.FromModule == "Facade"
                            && edge.RequestedModule == "Math"
                    && edge.IsExported
                    && edge.IsResolved);
    }


    [Fact]
    public void ManifestBackedAsmLibrariesResolveWithoutSourceFilesAndStayAbiDeclarations()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-asm-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libSyscall.starkpkg.json");
        var syscallPath = Path.Combine(tempDirectory.FullName, "Syscall.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Syscall.lib" : "libSyscall.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Syscall

                    public unsafe ffi asm(x86_64) fn i64[min max] Syscall0(i64[min max] number)
                        in("rax") number,
                        out("rax") return,
                        clobber("rcx", "r11")
                    {
                        "syscall"
                    }
                    """,
                    syscallPath),
                new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

            Assert.True(libraryResult.Succeeded);

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(syscallPath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Syscall
                    module Demo

                    fn i64[min max] Run() {
                        unsafe {
                            return Syscall.Syscall0(39);
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null),
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Syscall", out var importedModule));
            Assert.NotNull(importedModule);

            var importedAsm = Assert.Single(importedModule.SyntaxModel.Declarations, static declaration => declaration.Name == "Syscall0");
            Assert.NotNull(importedAsm.Function);
            Assert.NotNull(importedAsm.Function!.Asm);
            Assert.Equal("x86_64", importedAsm.Function.Asm!.ArchitectureText);
            Assert.Equal("syscall", importedAsm.Function.Asm.TemplateText);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
            Assert.NotNull(hir);
            Assert.Contains(
                hir.Functions,
                function => function.Name == "Syscall.Syscall0"
                    && function.BodyLoweringKind == FunctionBodyLoweringKind.AsmBypass);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
            Assert.NotNull(llvmModule);
            Assert.Contains("declare i64 @Syscall0(i64)", llvmModule.Text);
            Assert.Contains("call i64 @Syscall0(i64 39)", llvmModule.Text);
            Assert.DoesNotContain("asm sideeffect", llvmModule.Text);
            Assert.DoesNotContain("; imported asm definition: Syscall.Syscall0", llvmModule.Text);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedAsmLibrariesRejectMismatchedTargetArchitectures()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-asm-target-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libSyscall.starkpkg.json");
        var syscallPath = Path.Combine(tempDirectory.FullName, "Syscall.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Syscall.lib" : "libSyscall.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    module Syscall

                    public unsafe ffi asm(x86_64) fn i64[min max] Syscall0(i64[min max] number)
                        in("rax") number,
                        out("rax") return,
                        clobber("rcx", "r11")
                    {
                        "syscall"
                    }
                    """,
                    syscallPath),
                new CompilerOptions(TargetInfo: new LlvmTargetInfo("x86_64-unknown-linux-gnu", null)));

            Assert.True(libraryResult.Succeeded);

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(syscallPath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Syscall
                    module Demo

                    fn i64[min max] Run() {
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    TargetInfo: new LlvmTargetInfo("aarch64-unknown-linux-gnu", null),
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.False(consumerResult.Succeeded);
            Assert.Contains(
                consumerResult.Diagnostics,
                diagnostic => diagnostic.Code == "STK2102"
                    && diagnostic.Message.Contains("Syscall0", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("aarch64", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedStrictFpFunctionsPreserveModifierAndEffects()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-strictfp-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public strictfp finite law f32 Add(f32 left, f32 right) {
                    return left + right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var addFunction = Assert.Single(facadeModule.SourceSurface!.Functions!, static function => function.Name == "Add");
            Assert.True(addFunction.IsStrictFp);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn f32 Run() {
                        return Facade.Add(1.0, 2.0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);

            var importedAdd = Assert.Single(importedModule.SyntaxModel.Declarations, static declaration => declaration.Name == "Add");
            Assert.NotNull(importedAdd.Function);
            Assert.True(importedAdd.Function!.Modifiers.IsStrictFp);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
            Assert.NotNull(effectModel);
            Assert.True(effectModel.Functions["Facade.Add"].IsStrictFp);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedDoctrineLibrariesResolveWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-doctrine-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public doctrine Numbers {
                    finite law i32[min max] Double(i32[min max] value) {
                        return value + value;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded);

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        return Facade.Numbers.Double(3);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.NamedTypes.TryGetValue("Facade.Numbers", out var doctrineType));
            Assert.NotNull(doctrineType);
            Assert.Equal(DeclarationKind.Doctrine, doctrineType.Kind);
            Assert.True(typeCheckModel.Functions.ContainsKey("Facade.Numbers.Double"));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedDoctrineMethodsResolveFromPackageImageFactsWhenBridgeSignatureSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-doctrine-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public doctrine Numbers {
                    finite law i32[min max] Double(i32[min max] value) {
                        return value + value;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json"),
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Numbers.Double", importedDocument.PackageImageFacts!.FunctionSignatures.Keys);

            var corruptedSourceText = importedDocument.ParseResult.SourceText.Replace(
                StrictIntegerSource("finite law i32 Double(i32 value);"),
                "finite law Missing Double(Missing value);",
                StringComparison.Ordinal);
            Assert.NotEqual(importedDocument.ParseResult.SourceText, corruptedSourceText);

            var corruptedDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(corruptedSourceText)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        return Facade.Numbers.Double(3);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.Functions.ContainsKey("Facade.Numbers.Double"));
            Assert.Equal("i32", typeCheckModel.Functions["Facade.Numbers.Double"].ReturnType.DisplayName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedTraitLibrariesResolveWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-trait-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public trait Comparable {
                    law i32[min max] Compare(i32[min max] other);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded);

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.NamedTypes.TryGetValue("Facade.Comparable", out var traitType));
            Assert.NotNull(traitType);
            Assert.Equal(DeclarationKind.Trait, traitType.Kind);
            Assert.True(typeCheckModel.Functions.ContainsKey("Facade.Comparable.Compare"));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedTraitMethodsResolveFromPackageImageFactsWhenBridgeSignatureSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-trait-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public trait Comparable {
                    law i32[min max] Compare(i32[min max] other);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json"),
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Comparable.Compare", importedDocument.PackageImageFacts!.FunctionSignatures.Keys);

            var corruptedSourceText = importedDocument.ParseResult.SourceText.Replace(
                StrictIntegerSource("law i32 Compare(i32 other);"),
                "law Missing Compare(Missing other);",
                StringComparison.Ordinal);
            Assert.NotEqual(importedDocument.ParseResult.SourceText, corruptedSourceText);

            var corruptedDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(corruptedSourceText)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.Functions.ContainsKey("Facade.Comparable.Compare"));
            Assert.Equal("i32", typeCheckModel.Functions["Facade.Comparable.Compare"].ReturnType.DisplayName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedTraitAndDoctrineOptimizationRulesStayAbiBounded()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-closed-world-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public doctrine Numbers {
                    finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                        return left + right;
                    }
                }

                public trait Comparable {
                    law i32[min max] Compare(i32[min max] other);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded);

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        return Facade.Numbers.Add(1, 2);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.ClosedWorldOptimization, out ClosedWorldOptimizationModel? closedWorld));
            Assert.NotNull(closedWorld);

            Assert.Equal(ClosedWorldSealKind.AbiBoundary, closedWorld.Types["Facade.Numbers"].Seal);
            Assert.Equal(ClosedWorldSealKind.AbiBoundary, closedWorld.Types["Facade.Comparable"].Seal);
            Assert.False(closedWorld.Types["Facade.Numbers"].HasRuntimeDispatch);
            Assert.False(closedWorld.Types["Facade.Comparable"].HasRuntimeDispatch);

            Assert.Equal(
                new[] { ClosedWorldCallLoweringStrategy.DirectAbiBoundary },
                closedWorld.Functions["Facade.Numbers.Add"].SelectionOrder);
            Assert.Equal(ClosedWorldCodeGenerationMode.SharedCode, closedWorld.Functions["Facade.Numbers.Add"].CodeGenerationMode);
            Assert.True(closedWorld.Functions["Facade.Numbers.Add"].CanDevirtualize);

            Assert.Equal(
                new[] { ClosedWorldCallLoweringStrategy.CompileTimeOnlyContract },
                closedWorld.Functions["Facade.Comparable.Compare"].SelectionOrder);
            Assert.Equal(ClosedWorldCodeGenerationMode.MonomorphizationDeferred, closedWorld.Functions["Facade.Comparable.Compare"].CodeGenerationMode);
            Assert.False(closedWorld.Functions["Facade.Comparable.Compare"].CanDevirtualize);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedEnumsPreserveVariantShapesWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-enum-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Token {
                    End,
                    Integer(i32[min max]),
                    Move { X: i32[min max], Y: i32[min max] },
                }

                fn void Touch() {
                    return;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded);

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded);
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.NamedTypes.TryGetValue("Facade.Token", out var tokenEnum));
            Assert.NotNull(tokenEnum);
            Assert.Equal(DeclarationKind.Enum, tokenEnum.Kind);
            Assert.Equal(3, tokenEnum.Variants.Count);
            Assert.True(tokenEnum.Variants[0].IsUnit);
            Assert.Equal("Integer", tokenEnum.Variants[1].Name);
            Assert.Equal("i32", Assert.Single(tokenEnum.Variants[1].Fields).Type.DisplayName);
            Assert.True(tokenEnum.Variants[2].UsesNamedFields);
            Assert.Equal("X", tokenEnum.Variants[2].Fields[0].Name);
            Assert.Equal("Y", tokenEnum.Variants[2].Fields[1].Name);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedGenericEnumsCanBeInstantiatedWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-enum-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum IOResult<T> {
                    Ok(T),
                    Err(i32[min max]),
                }

                fn void Touch() {
                    return;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var ioResultManifest = Assert.Single(facadeModule.SourceSurface!.Types!, static type => type.Name == "IOResult");
            Assert.Equal(new[] { "T" }, ioResultManifest.GenericParameters);

            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32[min max] Unwrap(Facade.IOResult<i32[min max]> result) {
                        switch (result) {
                            case Facade.IOResult<i32[min max]>.Ok(var value):
                                return value;
                            case Facade.IOResult<i32[min max]>.Err(var code):
                                return code;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.NamedTypes.TryGetValue("Facade.IOResult", out var templateEnum));
            Assert.NotNull(templateEnum);
            Assert.True(templateEnum.IsGeneric);
            Assert.Equal(new[] { "T" }, templateEnum.GenericParams);
            Assert.True(typeCheckModel.NamedTypes.TryGetValue("Facade.IOResult<i32>", out var concreteEnum));
            Assert.NotNull(concreteEnum);
            Assert.Equal(DeclarationKind.Enum, concreteEnum.Kind);
            Assert.Equal("i32", Assert.Single(concreteEnum.Variants.Single(static variant => variant.Name == "Ok").Fields).Type.DisplayName);
            Assert.Equal("i32", Assert.Single(concreteEnum.Variants.Single(static variant => variant.Name == "Err").Fields).Type.DisplayName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void LiteralTypingAndBodyCheckingProduceTypedArtifacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Types

            struct Widget {
                i32[min max] Value;
            }

            public const Answer = 42;
            internal static rawptr<i8[min max]> Buffer = null;

            unsafe fn i32[min max] Run() {
                stack Widget widget = new Widget() { Value = 1 };
                stack i32[min max] value = widget.Value + 2;
                return value;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Widget"));
        Assert.True(typeCheckModel.Globals.ContainsKey("Answer"));
        Assert.Equal("u8[42 42]", typeCheckModel.Globals["Answer"].Type.DisplayName);
        Assert.Equal("i32", typeCheckModel.Functions["Run"].ReturnType.DisplayName);
        Assert.Contains(typeCheckModel.Literals, literal => literal.LiteralText == "null" && literal.Type.Kind == StarkTypeKind.Null);
    }


    [Fact]
    public void EnumDeclarationsFlowIntoSyntaxAndTypeModels()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Enums

            public enum Result<T, E> {
                Ok(T),
                Err(E),
            }

            enum Token {
                End,
                Integer(i32[min max]),
                Move { X: i32[min max], Y: i32[min max] },
            }

            fn void Run() {
                return;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);
        Assert.Contains(syntaxModel.Declarations, declaration => declaration.Kind == DeclarationKind.Enum && declaration.Name == "Result");
        Assert.Contains(syntaxModel.Declarations, declaration => declaration.Kind == DeclarationKind.Enum && declaration.Name == "Token");

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(typeCheckModel.NamedTypes.TryGetValue("Result", out var resultEnum));
        Assert.NotNull(resultEnum);
        Assert.Equal(DeclarationKind.Enum, resultEnum.Kind);
        Assert.Equal(2, resultEnum.Variants.Count);
        Assert.Equal("Ok", resultEnum.Variants[0].Name);
        Assert.Equal("T", Assert.Single(resultEnum.Variants[0].Fields).Type.DisplayName);

        Assert.True(typeCheckModel.NamedTypes.TryGetValue("Token", out var tokenEnum));
        Assert.NotNull(tokenEnum);
        Assert.Equal(DeclarationKind.Enum, tokenEnum.Kind);
        Assert.Equal(3, tokenEnum.Variants.Count);
        Assert.True(tokenEnum.Variants[0].IsUnit);
        Assert.False(tokenEnum.Variants[1].UsesNamedFields);
        Assert.Equal("i32", Assert.Single(tokenEnum.Variants[1].Fields).Type.DisplayName);
        Assert.True(tokenEnum.Variants[2].UsesNamedFields);
        Assert.Equal("X", tokenEnum.Variants[2].Fields[0].Name);
        Assert.Equal("Y", tokenEnum.Variants[2].Fields[1].Name);
    }


    [Fact]
    public void InternalEnumsDeriveDirectTagLayoutsAndFlowThroughTheFullPipeline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Enums

            enum Token {
                End,
                Integer(i32[min max]),
                Move { X: i32[min max], Y: i32[min max] },
            }

            fn Token Make(i32[min max] value) {
                if (value == 0) {
                    return Token.End;
                }

                if (value == 1) {
                    return Token.Integer(7);
                }

                return Token.Move { X: value, Y: 2 };
            }

            fn Token Echo(Token token) {
                return token;
            }

            unsafe fn i32[min max] Run() {
                stack Token first = Token.Integer(5);
                stack Token second = Token.Move { X: 1, Y: 2 };
                stack Token third = Make(0);
                stack Token fourth = Echo(second);
                return 0;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
        Assert.NotNull(enumLayoutModel);
        Assert.True(enumLayoutModel.Layouts.TryGetValue("Token", out var tokenLayout));
        Assert.NotNull(tokenLayout);
        Assert.Equal(EnumLayoutKind.DirectTag, tokenLayout.Kind);
        Assert.Equal("$tag", tokenLayout.TagField.Name);
        Assert.Equal(4, tokenLayout.OrderedFields.Count);
        Assert.Equal("$Integer_0", tokenLayout.OrderedFields[1].Name);
        Assert.Equal("$Move_X", tokenLayout.OrderedFields[2].Name);
        Assert.Equal("$Move_Y", tokenLayout.OrderedFields[3].Name);

        Assert.True(tokenLayout.TryGetVariant("End", out var endVariant));
        Assert.Equal(0, endVariant.TagValue);
        Assert.Empty(endVariant.Fields);

        Assert.True(tokenLayout.TryGetVariant("Integer", out var integerVariant));
        Assert.Equal(1, integerVariant.TagValue);
        Assert.Equal("$Integer_0", Assert.Single(integerVariant.Fields).StorageFieldName);

        Assert.True(tokenLayout.TryGetVariant("Move", out var moveVariant));
        Assert.Equal(2, moveVariant.TagValue);
        Assert.Equal("X", moveVariant.Fields[0].SourceFieldName);
        Assert.Equal("Y", moveVariant.Fields[1].SourceFieldName);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("%Token = type {", llvmModule.Text, StringComparison.Ordinal);
    }


    [Fact]
    public void UnresolvedImportsFailBeforeTypingAndLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            import Missing.Module
            module Demo

            fn void Run() { return; }
            """));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK2000");
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? _));
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? _));
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.OwnershipValidation, out OwnershipValidationModel? _));
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "module-graph" && execution.Status == PassExecutionStatus.Executed);
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "type-check" && execution.Status == PassExecutionStatus.Skipped);
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "semantic-validate" && execution.Status == PassExecutionStatus.Skipped);
        Assert.Contains(
            result.Executions,
            execution => execution.PassId == "ownership-validate" && execution.Status == PassExecutionStatus.Skipped);
    }


    [Fact]
    public void InvalidTypedAssignmentsProduceTypeDiagnostics()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            public const i32[min max] Value = null;
            """));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK3002");
    }


    [Fact]
    public void DoctrineMethodsUseModuleQualifiedSymbolsWhenEmittingLibraries()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Math

                public doctrine Numbers {
                    finite law i32[min max] Add(i32[min max] left, i32[min max] right) {
                        return left + right;
                    }
                }
                """),
            new CompilerOptions(QualifyModuleSymbols: true));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
        Assert.NotNull(abiModel);
        Assert.Equal("Math.Numbers.Add", abiModel.Functions["Numbers.Add"].SymbolName);
    }


    [Fact]
    public void OverloadedMethodsResolveThroughSemanticValidationAndMirLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            struct Buffer {
                i32[min max] Value;

                fn i32[min max] Scale(borrow Buffer self, i32[min max] factor) {
                    return self.Value * factor;
                }

                fn i32[min max] Scale(borrow Buffer self, bool doubleIt) {
                    if (doubleIt) {
                        return self.Value * 2;
                    }

                    return self.Value;
                }
            }

            unsafe fn i32[min max] Run() {
                stack Buffer buffer = new Buffer() { Value = 3 };
                return buffer.Scale(4) + buffer.Scale(true);
            }
            """));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? semanticValidation));
        Assert.NotNull(semanticValidation);

        var calledOverloads = semanticValidation.Functions["Run"].CalledFunctions
            .Where(static name => name.StartsWith("Buffer.Scale#(", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, calledOverloads.Length);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.Equal(
            2,
            mir.Functions.Count(static function => function.Name.StartsWith("Buffer.Scale#(", StringComparison.Ordinal)));
    }


    [Fact]
    public void ManifestBackedOverloadedFunctionsResolveWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-overload-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public finite law i32[min max] Parse(i32[min max] value) {
                    return value;
                }

                public finite law i32[min max] Parse(bool value) {
                    if (value) {
                        return 1;
                    }

                    return 0;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var parseOverloads = facadeModule.SourceSurface!.Functions!
                .Where(static function => function.Name == "Parse")
                .ToArray();

            Assert.Equal(2, parseOverloads.Length);
            Assert.All(parseOverloads, static function => Assert.Equal("Facade.Parse", function.QualifiedName));
            Assert.Equal(
                2,
                parseOverloads.Select(static function => function.SymbolName).Distinct(StringComparer.Ordinal).Count());

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        return Facade.Parse(4) + Facade.Parse(true);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.Overloads.TryGetValue("Facade.Parse", out var overloads));
            Assert.Equal(2, overloads.Count);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? semanticValidation));
            Assert.NotNull(semanticValidation);

            var calledOverloads = semanticValidation.Functions["Run"].CalledFunctions
                .Where(static name => name.StartsWith("Facade.Parse#(", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(2, calledOverloads.Length);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedGenericFunctionsPreserveTypeParametersWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-function-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var identityManifest = Assert.Single(facadeModule.SourceSurface!.Functions!, static function => function.Name == "Identity");
            Assert.Equal(["T"], identityManifest.GenericParameters);

            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);

            var importedIdentity = Assert.Single(importedModule.SyntaxModel.Declarations, static declaration => declaration.Name == "Identity");
            Assert.NotNull(importedIdentity.Function);
            Assert.True(importedIdentity.Function!.IsGeneric);
            Assert.Equal(["T"], importedIdentity.Function.GenericParams);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesStructuredTypedInterfaceSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-interface-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }
                public alias BufferView<T> = Pair<T>[];
                public fn Pair<i32[min max]> First(BufferView<i32[min max]> view);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var typedInterface = facadeModule.CompilerSections!.TypedInterface!;

            var pair = Assert.Single(typedInterface.Types, static type => type.Name == "Pair");
            Assert.Equal(["T"], pair.GenericParameters);
            var pairField = Assert.Single(pair.Fields);
            Assert.Equal("named", pairField.Type.Kind);
            Assert.Equal("T", pairField.Type.Name);

            var bufferView = Assert.Single(typedInterface.TypeAliases!, static alias => alias.Name == "BufferView");
            Assert.Equal(["T"], bufferView.GenericParameters);
            Assert.Equal("slice", bufferView.TargetType.Kind);
            Assert.NotNull(bufferView.TargetType.ElementType);
            Assert.Equal("named", bufferView.TargetType.ElementType!.Kind);
            Assert.Equal("Pair", bufferView.TargetType.ElementType.Name);
            Assert.NotNull(bufferView.TargetType.ElementType.TypeArguments);
            Assert.Equal("T", Assert.Single(bufferView.TargetType.ElementType.TypeArguments!).Name);

            var first = Assert.Single(typedInterface.Functions, static function => function.Name == "First");
            Assert.Equal("named", first.ReturnType.Kind);
            Assert.Equal("Pair", first.ReturnType.Name);
            var returnTypeArgument = Assert.Single(first.ReturnType.TypeArguments!);
            Assert.Equal("integer", returnTypeArgument.Kind);
            Assert.Equal(32, returnTypeArgument.BitWidth);

            var viewParameter = Assert.Single(first.Parameters);
            Assert.Equal("slice", viewParameter.Type.Kind);
            Assert.NotNull(viewParameter.Type.ElementType);
            Assert.Equal("named", viewParameter.Type.ElementType!.Kind);
            Assert.Equal("Pair", viewParameter.Type.ElementType!.Name);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesExplicitSourceSurfaceSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-surface-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }
                public alias BufferView<T> = Pair<T>[];
                public alias IntBufferView = Pair<i32[min max]>[];
                public record Holder(IntBufferView View) {
                    IntBufferView Cached;

                    fn IntBufferView Echo(IntBufferView value);
                }

                public fn BufferView<i32[min max]> First(BufferView<i32[min max]> view);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.SourceSurface);

            var sourceSurface = facadeModule.SourceSurface!;
            Assert.Empty(facadeModule.ReExports);
            Assert.Empty(facadeModule.Functions);
            Assert.Empty(facadeModule.Types);
            Assert.Empty(facadeModule.Globals);
            Assert.True(facadeModule.TypeAliases is null || facadeModule.TypeAliases.Count == 0);
            Assert.True(facadeModule.Imports is null || facadeModule.Imports.Count == 0);
            var pair = Assert.Single(sourceSurface.Types!, static type => type.Name == "Pair");
            Assert.Equal("record", pair.Kind);
            Assert.Equal(["T"], pair.GenericParameters);

            var holder = Assert.Single(sourceSurface.Types!, static type => type.Name == "Holder");
            var primaryConstructorParameter = Assert.Single(holder.PrimaryConstructorParameters!);
            Assert.Equal("IntBufferView", primaryConstructorParameter.Type);
            var holderField = Assert.Single(holder.Fields);
            Assert.Equal("IntBufferView", holderField.Type);
            var holderMethod = Assert.Single(holder.Methods!);
            Assert.Equal("IntBufferView", holderMethod.ReturnType);
            Assert.Equal("IntBufferView", Assert.Single(holderMethod.Parameters).Type);

            var bufferView = Assert.Single(sourceSurface.TypeAliases!, static alias => alias.Name == "BufferView");
            Assert.Equal("Pair<T>[]", bufferView.TargetType);
            Assert.Equal(["T"], bufferView.GenericParameters);

            var intBufferView = Assert.Single(sourceSurface.TypeAliases!, static alias => alias.Name == "IntBufferView");
            Assert.Equal(StrictIntegerSource("Pair<i32>[]"), intBufferView.TargetType);

            var first = Assert.Single(sourceSurface.Functions!, static function => function.Name == "First");
            Assert.Equal(StrictIntegerSource("BufferView<i32>"), first.ReturnType);
            var parameter = Assert.Single(first.Parameters);
            Assert.Equal(StrictIntegerSource("BufferView<i32>"), parameter.Type);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageImageSourceBridgeFallsBackToExplicitSourceSurfaceSectionsWhenTypedInterfaceIsMissing()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: null,
            SourceSurface: new StarkPackageSourceSurfaceSection(
                Imports:
                [
                    new StarkPackageImportManifest("Bits", IsExported: false)
                ],
                ReExports: [],
                Functions:
                [
                    new StarkPackageFunctionManifest(
                        Name: "Identity",
                        QualifiedName: "Facade.Identity",
                        Visibility: "public",
                        SymbolName: "Facade.Identity",
                        Kind: "fn",
                        ReturnType: "BufferView",
                        Parameters:
                        [
                            new StarkPackageParameterManifest("value", "BufferView")
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases:
                [
                    new StarkPackageTypeAliasManifest(
                        Name: "BufferView",
                        QualifiedName: "Facade.BufferView",
                        Visibility: "public",
                        TargetType: StrictIntegerSource("i32[]"))
                ]));

        Assert.True(
            PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var sourceText));

        Assert.Contains("import Bits", sourceText, StringComparison.Ordinal);
        Assert.Contains(StrictIntegerSource("public alias BufferView = i32[];"), sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn BufferView Identity(BufferView value);", sourceText, StringComparison.Ordinal);
    }


    [Fact]
    public void PackageImageSourceBridgePrefersExplicitSourceSurfaceOverLegacyFlatSurfaceFields()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports:
            [
                new StarkPackageReExportManifest("WrongBits")
            ],
            Functions:
            [
                new StarkPackageFunctionManifest(
                    Name: "Wrong",
                    QualifiedName: "Facade.Wrong",
                    Visibility: "public",
                    SymbolName: "Facade.Wrong",
                    Kind: "fn",
                    ReturnType: "i8",
                    Parameters: [],
                    IsFfi: false,
                    IsStrictFp: false,
                    UseFastCallingConvention: true)
            ],
            Types: [],
            Globals: [],
            TypeAliases:
            [
                new StarkPackageTypeAliasManifest(
                    Name: "WrongBuffer",
                    QualifiedName: "Facade.WrongBuffer",
                    Visibility: "public",
                    TargetType: "i8[]")
            ],
            Imports:
            [
                new StarkPackageImportManifest("WrongBits", IsExported: false)
            ],
            TypedInterface: null,
            SourceSurface: new StarkPackageSourceSurfaceSection(
                Imports:
                [
                    new StarkPackageImportManifest("Bits", IsExported: false)
                ],
                ReExports: [],
                Functions:
                [
                    new StarkPackageFunctionManifest(
                        Name: "Right",
                        QualifiedName: "Facade.Right",
                        Visibility: "public",
                        SymbolName: "Facade.Right",
                        Kind: "fn",
                        ReturnType: StrictIntegerSource("i32"),
                        Parameters: [],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases:
                [
                    new StarkPackageTypeAliasManifest(
                        Name: "Buffer",
                        QualifiedName: "Facade.Buffer",
                        Visibility: "public",
                        TargetType: StrictIntegerSource("i32[]"))
                ]));

        Assert.True(
            PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var sourceText));

        Assert.Contains("import Bits", sourceText, StringComparison.Ordinal);
        Assert.Contains(StrictIntegerSource("public alias Buffer = i32[];"), sourceText, StringComparison.Ordinal);
        Assert.Contains(StrictIntegerSource("public fn i32 Right();"), sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("WrongBits", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("WrongBuffer", sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("public fn i8 Wrong();", sourceText, StringComparison.Ordinal);
    }


    [Fact]
    public void PackageManifestIncludesExplicitCompilerSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-compiler-sections-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T Identity<T>(T value) {
                    stack T copy = value;
                    return copy;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");

            Assert.NotNull(facadeModule.CompilerSections);
            Assert.NotNull(facadeModule.CompilerSections!.TypedInterface);
            Assert.NotNull(facadeModule.CompilerSections.CompilerFacts);
            Assert.NotNull(facadeModule.CompilerSections.GenericTemplates);
            Assert.Null(facadeModule.TypedInterface);
            Assert.Null(facadeModule.CompilerFacts);
            Assert.Null(facadeModule.GenericTemplates);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageImageSourceBridgePrefersExplicitCompilerSectionsOverLegacyFlatFields()
    {
        var legacyTypedInterface = new StarkPackageTypedInterfaceSection(
            Functions:
            [
                new StarkPackageTypedFunctionManifest(
                    Name: "Wrong",
                    QualifiedName: "Facade.Wrong",
                    Visibility: "public",
                    SymbolName: "Facade.Wrong",
                    Kind: "fn",
                    ReturnType: new StarkPackageTypeReference("integer", BitWidth: 8),
                    Parameters: [],
                    IsFfi: false,
                    IsStrictFp: false,
                    UseFastCallingConvention: true)
            ],
            Types: [],
            Globals: []);
        var explicitTypedInterface = new StarkPackageTypedInterfaceSection(
            Functions:
            [
                new StarkPackageTypedFunctionManifest(
                    Name: "Right",
                    QualifiedName: "Facade.Right",
                    Visibility: "public",
                    SymbolName: "Facade.Right",
                    Kind: "fn",
                    ReturnType: SignedIntegerTypeReference(32),
                    Parameters: [],
                    IsFfi: false,
                    IsStrictFp: false,
                    UseFastCallingConvention: true)
            ],
            Types: [],
            Globals: []);
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: legacyTypedInterface,
            CompilerSections: new StarkPackageCompilerSectionsManifest(
                TypedInterface: explicitTypedInterface));

        Assert.True(
            PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var sourceText));

        Assert.Contains(StrictIntegerSource("public fn i32 Right();"), sourceText, StringComparison.Ordinal);
        Assert.DoesNotContain("Wrong", sourceText, StringComparison.Ordinal);
    }


    [Fact]
    public void PackageImageSourceBridgeOmitsPrivateSourceImportsWhenTypedInterfaceIsEnough()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "Identity",
                        QualifiedName: "Facade.Identity",
                        Visibility: "public",
                        SymbolName: "Facade.Identity",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference("named", Name: "Bits.Token"),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "value",
                                new StarkPackageTypeReference("named", Name: "Bits.Token"))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases: []),
            SourceSurface: new StarkPackageSourceSurfaceSection(
                Imports:
                [
                    new StarkPackageImportManifest("Bits", IsExported: false)
                ],
                ReExports: [],
                Functions:
                [
                    new StarkPackageFunctionManifest(
                        Name: "Identity",
                        QualifiedName: "Facade.Identity",
                        Visibility: "public",
                        SymbolName: "Facade.Identity",
                        Kind: "fn",
                        ReturnType: "Token",
                        Parameters:
                        [
                            new StarkPackageParameterManifest("value", "Token")
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases: []));

        Assert.True(
            PackageImageLoader.TryBuildModuleSource(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var sourceText));

        Assert.DoesNotContain("import Bits", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn Bits.Token Identity(Bits.Token value);", sourceText, StringComparison.Ordinal);
    }


    [Fact]
    public void PackageImageFactsPreferExplicitCompilerSectionsOverLegacyFlatFields()
    {
        var legacyCompilerFacts = new StarkPackageCompilerFactsSection(
            FunctionEffects:
            [
                new StarkPackageFunctionEffectManifest(
                    QualifiedResolvedName: "Facade.Touch",
                    Kind: "fn",
                    ReadsArgumentMemory: true,
                    IsPure: false,
                    NoSync: true,
                    NoFree: true,
                    NoUnwind: true,
                    WillReturn: true,
                    MustProgress: true,
                    UseFastCallingConvention: true,
                    IsFfi: false,
                    IsHot: false,
                    IsCold: true,
                    InlinePreference: "noinline",
                    IsStrictFp: false)
            ]);
        var explicitCompilerFacts = new StarkPackageCompilerFactsSection(
            FunctionEffects:
            [
                new StarkPackageFunctionEffectManifest(
                    QualifiedResolvedName: "Facade.Touch",
                    Kind: "fn",
                    ReadsArgumentMemory: false,
                    IsPure: true,
                    NoSync: true,
                    NoFree: true,
                    NoUnwind: true,
                    WillReturn: true,
                    MustProgress: true,
                    UseFastCallingConvention: true,
                    IsFfi: false,
                    IsHot: true,
                    IsCold: false,
                    InlinePreference: "inline",
                    IsStrictFp: true)
            ]);
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            CompilerFacts: legacyCompilerFacts,
            CompilerSections: new StarkPackageCompilerSectionsManifest(
                CompilerFacts: explicitCompilerFacts));

        Assert.True(
            PackageImageLoader.TryBuildLoadedPackageImageFacts(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var facts));

        var effect = facts.FunctionEffects["Facade.Touch"];
        Assert.True(effect.IsHot);
        Assert.False(effect.IsCold);
        Assert.True(effect.IsPure);
        Assert.False(effect.ReadsArgumentMemory);
        Assert.True(effect.IsStrictFp);
        Assert.Equal(InlinePreference.Inline, effect.InlinePreference);
    }


    [Fact]
    public void PackageImageSourceBridgeKeepsSupportedGenericTemplateBodiesDeclarationOnlyWhenTypedInterfaceIsPresent()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-bridge-surface-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public alias Count = i32[min max];

                public fn Count Identity<T>(Count value) {
                    return value + 0;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var identityTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Identity");
            Assert.True(identityTemplate.EstimatedBodyCost is > 0);
            Assert.Contains("\"EstimatedBodyCost\"", manifest.ToJson(), StringComparison.Ordinal);

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        Path.Combine(tempDirectory.FullName, "Facade.starkpkg.json"),
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains(StrictIntegerSource("public alias Count = i32;"), sourceText, StringComparison.Ordinal);
            Assert.Contains(StrictIntegerSource("public fn i32 Identity<T>(i32 value);"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain(StrictIntegerSource("public fn i32 Identity<T>(i32 value) {"), sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return value + 0;", sourceText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void StructuredPackageImageDocumentsKeepTypedLoopControlGenericDeclarationsWhenBodyTextIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-structured-loop-control-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] SumWhileControl<T>(i32[min max] count, i32[min max] stopAt, T tag) {
                    stack mut i32[min max] sum = 0;
                    stack mut i32[min max] index = 0;
                    while willexit (index < count) {
                        index = index + 1;
                        if (index < 2) {
                            continue;
                        }
                        if (index == stopAt) {
                            break;
                        }
                        sum = sum + index;
                    }
                    return sum;
                }

                public fn i32[min max] SumForControl<T>(i32[min max] count, i32[min max] stopAt, T tag) {
                    stack mut i32[min max] sum = 0;
                    for willexit (stack mut i32[min max] index = 0; index < count; index = index + 1) {
                        if (index < 2) {
                            continue;
                        }
                        if (index == stopAt) {
                            break;
                        }
                        sum = sum + index;
                    }
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.GenericTemplates);

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                facadeModule.GenericTemplates!.Functions
                                    .Select(template => template with
                                    {
                                        BodyText = "{ return this is not valid Stark; }"
                                    })
                                    .ToArray()),
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: new StarkPackageGenericTemplateSection(
                                    facadeModule.GenericTemplates!.Functions
                                        .Select(template => template with
                                        {
                                            BodyText = "{ return this is not valid Stark; }"
                                        })
                                        .ToArray())),
                            SourceSurface = new StarkPackageSourceSurfaceSection(
                                Imports: facadeModule.EffectiveSourceSurface.Imports,
                                ReExports: facadeModule.EffectiveSourceSurface.ReExports,
                                Functions: [],
                                Types: [],
                                Globals: [],
                                TypeAliases: [])
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildStructuredModuleDocument(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var importedDocument));

            Assert.DoesNotContain("this is not valid Stark", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains(StrictIntegerSource("public fn i32 SumWhileControl<T>(i32 count, i32 stopAt, T tag);"), importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains(StrictIntegerSource("public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag);"), importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("while willexit (index < count)", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (stack mut i32 index = 0; index < count; index = index + 1)", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("continue;", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("break;", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageImageSyntaxModelCarriesPublishedOverloadKeysFromSourceSurface()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "First",
                        QualifiedName: "Facade.First",
                        Visibility: "public",
                        SymbolName: "Facade.First",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference(
                            "slice",
                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "view",
                                new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types:
                [
                    new StarkPackageTypedTypeManifest(
                        Name: "Box",
                        QualifiedName: "Facade.Box",
                        Visibility: "public",
                        Kind: "record",
                        Fields:
                        [
                            new StarkPackageTypedFieldManifest(
                                "Values",
                                new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                        ],
                        Methods:
                        [
                            new StarkPackageTypedMethodManifest(
                                Name: "Echo",
                                QualifiedName: "Facade.Box.Echo",
                                SymbolName: "Facade.Box.Echo",
                                Kind: "fn",
                                ReturnType: new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)),
                                Parameters:
                                [
                                    new StarkPackageTypedParameterManifest(
                                        "self",
                                        new StarkPackageTypeReference(
                                            "named",
                                            BorrowKind: "borrow",
                                            Name: "Box")),
                                    new StarkPackageTypedParameterManifest(
                                        "view",
                                        new StarkPackageTypeReference(
                                            "slice",
                                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                                ],
                                IsFfi: false,
                                IsStrictFp: false,
                                UseFastCallingConvention: true)
                        ])
                ],
                Globals: [],
                TypeAliases:
                [
                    new StarkPackageTypedTypeAliasManifest(
                        Name: "BufferView",
                        QualifiedName: "Facade.BufferView",
                        Visibility: "public",
                        TargetType: new StarkPackageTypeReference(
                            "slice",
                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                ]),
            SourceSurface: new StarkPackageSourceSurfaceSection(
                Functions:
                [
                    new StarkPackageFunctionManifest(
                        Name: "First",
                        QualifiedName: "Facade.First",
                        Visibility: "public",
                        SymbolName: "Facade.First",
                        Kind: "fn",
                        ReturnType: "BufferView",
                        Parameters:
                        [
                            new StarkPackageParameterManifest("view", "BufferView")
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types:
                [
                    new StarkPackageTypeManifest(
                        Name: "Box",
                        QualifiedName: "Facade.Box",
                        Visibility: "public",
                        Kind: "record",
                        Fields:
                        [
                            new StarkPackageFieldManifest("Values", "BufferView")
                        ],
                        Methods:
                        [
                            new StarkPackageMethodManifest(
                                Name: "Echo",
                                QualifiedName: "Facade.Box.Echo",
                                SymbolName: "Facade.Box.Echo",
                                Kind: "fn",
                                ReturnType: "BufferView",
                                Parameters:
                                [
                                    new StarkPackageParameterManifest("self", "borrow Box"),
                                    new StarkPackageParameterManifest("view", "BufferView")
                                ],
                                IsFfi: false,
                                IsStrictFp: false,
                                UseFastCallingConvention: true)
                        ])
                ],
                TypeAliases:
                [
                    new StarkPackageTypeAliasManifest(
                        Name: "BufferView",
                        QualifiedName: "Facade.BufferView",
                        Visibility: "public",
                        TargetType: "i32[]")
                ]));

        Assert.True(
            PackageImageLoader.TryBuildModuleSyntaxModel(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var syntaxModel));

        var first = Assert.Single(
            syntaxModel.Declarations,
            static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "First");
        Assert.NotNull(first.Function);
        Assert.Equal("(BufferView)", first.Function!.PublishedOverloadKey);

        var echo = Assert.Single(
            syntaxModel.Declarations,
            static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Box.Echo");
        Assert.NotNull(echo.Function);
        Assert.Equal("(borrowBox,BufferView)", echo.Function!.PublishedOverloadKey);
    }


    [Fact]
    public void PackageImageSyntaxModelCarriesPublishedOverloadKeysFromLegacyFlatSurfaceFieldsWhenExplicitSourceSurfaceIsMissing()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions:
            [
                new StarkPackageFunctionManifest(
                    Name: "First",
                    QualifiedName: "Facade.First",
                    Visibility: "public",
                    SymbolName: "Facade.First",
                    Kind: "fn",
                    ReturnType: "BufferView",
                    Parameters:
                    [
                        new StarkPackageParameterManifest("view", "BufferView")
                    ],
                    IsFfi: false,
                    IsStrictFp: false,
                    UseFastCallingConvention: true)
            ],
            Types:
            [
                new StarkPackageTypeManifest(
                    Name: "Box",
                    QualifiedName: "Facade.Box",
                    Visibility: "public",
                    Kind: "record",
                    Fields:
                    [
                        new StarkPackageFieldManifest("Values", "BufferView")
                    ],
                    Methods:
                    [
                        new StarkPackageMethodManifest(
                            Name: "Echo",
                            QualifiedName: "Facade.Box.Echo",
                            SymbolName: "Facade.Box.Echo",
                            Kind: "fn",
                            ReturnType: "BufferView",
                            Parameters:
                            [
                                new StarkPackageParameterManifest("self", "borrow Box"),
                                new StarkPackageParameterManifest("view", "BufferView")
                            ],
                            IsFfi: false,
                            IsStrictFp: false,
                            UseFastCallingConvention: true)
                    ])
            ],
            Globals: [],
            TypeAliases:
            [
                new StarkPackageTypeAliasManifest(
                    Name: "BufferView",
                    QualifiedName: "Facade.BufferView",
                    Visibility: "public",
                    TargetType: "i32[]")
            ],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "First",
                        QualifiedName: "Facade.First",
                        Visibility: "public",
                        SymbolName: "Facade.First",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference(
                            "slice",
                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "view",
                                new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types:
                [
                    new StarkPackageTypedTypeManifest(
                        Name: "Box",
                        QualifiedName: "Facade.Box",
                        Visibility: "public",
                        Kind: "record",
                        Fields:
                        [
                            new StarkPackageTypedFieldManifest(
                                "Values",
                                new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                        ],
                        Methods:
                        [
                            new StarkPackageTypedMethodManifest(
                                Name: "Echo",
                                QualifiedName: "Facade.Box.Echo",
                                SymbolName: "Facade.Box.Echo",
                                Kind: "fn",
                                ReturnType: new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)),
                                Parameters:
                                [
                                    new StarkPackageTypedParameterManifest(
                                        "self",
                                        new StarkPackageTypeReference(
                                            "named",
                                            BorrowKind: "borrow",
                                            Name: "Box")),
                                    new StarkPackageTypedParameterManifest(
                                        "view",
                                        new StarkPackageTypeReference(
                                            "slice",
                                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                                ],
                                IsFfi: false,
                                IsStrictFp: false,
                                UseFastCallingConvention: true)
                        ])
                ],
                Globals: [],
                TypeAliases:
                [
                    new StarkPackageTypedTypeAliasManifest(
                        Name: "BufferView",
                        QualifiedName: "Facade.BufferView",
                        Visibility: "public",
                        TargetType: new StarkPackageTypeReference(
                            "slice",
                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                ]),
            SourceSurface: null);

        Assert.True(
            PackageImageLoader.TryBuildModuleSyntaxModel(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var syntaxModel));

        var first = Assert.Single(
            syntaxModel.Declarations,
            static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "First");
        Assert.NotNull(first.Function);
        Assert.Equal("(BufferView)", first.Function!.PublishedOverloadKey);

        var echo = Assert.Single(
            syntaxModel.Declarations,
            static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Box.Echo");
        Assert.NotNull(echo.Function);
        Assert.Equal("(borrowBox,BufferView)", echo.Function!.PublishedOverloadKey);
    }


    [Fact]
    public void PackageImageSyntaxModelCarriesPublishedOverloadKeysFromTypedInterfaceWhenSourceSurfaceFunctionEntriesAreMissing()
    {
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "First",
                        QualifiedName: "Facade.First",
                        Visibility: "public",
                        SymbolName: "Facade.First",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference(
                            "slice",
                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "view",
                                new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true,
                        PublishedOverloadKey: "(BufferView)",
                        HasGenericTemplateBody: true)
                ],
                Types:
                [
                    new StarkPackageTypedTypeManifest(
                        Name: "Box",
                        QualifiedName: "Facade.Box",
                        Visibility: "public",
                        Kind: "record",
                        Fields:
                        [
                            new StarkPackageTypedFieldManifest(
                                "Values",
                                new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                        ],
                        Methods:
                        [
                            new StarkPackageTypedMethodManifest(
                                Name: "Echo",
                                QualifiedName: "Facade.Box.Echo",
                                SymbolName: "Facade.Box.Echo",
                                Kind: "fn",
                                ReturnType: new StarkPackageTypeReference(
                                    "slice",
                                    ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)),
                                Parameters:
                                [
                                    new StarkPackageTypedParameterManifest(
                                        "self",
                                        new StarkPackageTypeReference(
                                            "named",
                                            BorrowKind: "borrow",
                                            Name: "Box")),
                                    new StarkPackageTypedParameterManifest(
                                        "view",
                                        new StarkPackageTypeReference(
                                            "slice",
                                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                                ],
                                IsFfi: false,
                                IsStrictFp: false,
                                UseFastCallingConvention: true,
                                PublishedOverloadKey: "(borrowBox,BufferView)",
                                HasGenericTemplateBody: true)
                        ])
                ],
                Globals: [],
                TypeAliases:
                [
                    new StarkPackageTypedTypeAliasManifest(
                        Name: "BufferView",
                        QualifiedName: "Facade.BufferView",
                        Visibility: "public",
                        TargetType: new StarkPackageTypeReference(
                            "slice",
                            ElementType: new StarkPackageTypeReference("integer", BitWidth: 32)))
                ]),
            SourceSurface: new StarkPackageSourceSurfaceSection(
                Functions: [],
                Types: [],
                Globals: [],
                TypeAliases: []));

        Assert.True(
            PackageImageLoader.TryBuildModuleSyntaxModel(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var syntaxModel));

        var first = Assert.Single(
            syntaxModel.Declarations,
            static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "First");
        Assert.NotNull(first.Function);
        Assert.True(first.Function!.HasBody);
        Assert.Equal("(BufferView)", first.Function.PublishedOverloadKey);

        var echo = Assert.Single(
            syntaxModel.Declarations,
            static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Box.Echo");
        Assert.NotNull(echo.Function);
        Assert.True(echo.Function!.HasBody);
        Assert.Equal("(borrowBox,BufferView)", echo.Function.PublishedOverloadKey);
    }


    [Fact]
    public void DeclaredFunctionSyntaxCollectorMatchesPublishedOverloadKeysFromTypedInterfaceSyntaxModels()
    {
        var parseResult = StarkSyntax.ParseCompilationUnit(
            """
            module Facade

            public alias BufferView = i32[min max][];

            public fn BufferView First(BufferView view);

            public record Box(BufferView Values) {
                fn BufferView Echo(borrow Box self, BufferView view);
            }
            """);

        var syntaxModel = new SyntaxModel(
            "Facade",
            [],
            [
                new TopLevelDeclarationModel(
                    "BufferView",
                    DeclarationKind.TypeAlias,
                    StarkVisibility.Public,
                    Function: null,
                    TypeAlias: new TypeAliasDeclarationModel("BufferView", "i32[]", [])),
                new TopLevelDeclarationModel(
                    "First",
                    DeclarationKind.Function,
                    StarkVisibility.Public,
                    new FunctionDeclarationModel(
                        Name: "First",
                        Kind: StarkFunctionKind.Fn,
                        ReturnType: "i32[]",
                        Parameters:
                        [
                            new ParameterModel("view", "i32[]")
                        ],
                        Modifiers: new FunctionModifierSet(
                            InlinePreference.InlineHint,
                            HasExplicitInlinePreference: false,
                            IsHot: false,
                            IsCold: false,
                            IsFfi: false,
                            IsVarargs: false,
                            IsStrictFp: false),
                        HasBody: false,
                        PublishedOverloadKey: "(BufferView)")),
                new TopLevelDeclarationModel(
                    "Box",
                    DeclarationKind.Record,
                    StarkVisibility.Public,
                    Function: null),
                new TopLevelDeclarationModel(
                    "Box.Echo",
                    DeclarationKind.Function,
                    StarkVisibility.Public,
                    new FunctionDeclarationModel(
                        Name: "Box.Echo",
                        Kind: StarkFunctionKind.Fn,
                        ReturnType: "i32[]",
                        Parameters:
                        [
                            new ParameterModel("self", "borrow Box"),
                            new ParameterModel("view", "i32[]")
                        ],
                        Modifiers: new FunctionModifierSet(
                            InlinePreference.InlineHint,
                            HasExplicitInlinePreference: false,
                            IsHot: false,
                            IsCold: false,
                            IsFfi: false,
                            IsVarargs: false,
                            IsStrictFp: false),
                        HasBody: false,
                        PublishedOverloadKey: "(borrowBox,BufferView)"))
            ]);

        var functions = DeclaredFunctionSyntaxCollector.Collect(parseResult, syntaxModel);

        Assert.Contains(functions, static function => function.SourceName == "First");
        Assert.Contains(functions, static function => function.SourceName == "Box.Echo");
        Assert.True(FunctionOverloadFacts.TryFindFunctionDeclaration(syntaxModel, "First", "(BufferView)", out _));
        Assert.True(FunctionOverloadFacts.TryFindFunctionDeclaration(syntaxModel, "Box.Echo", "(borrowBox,BufferView)", out _));
    }

    [Fact]
    public void PublishedOverloadKeysDriveResolvedNamesForPackageDeclarations()
    {
        static FunctionModifierSet Modifiers() => new(
            InlinePreference.InlineHint,
            HasExplicitInlinePreference: false,
            IsHot: false,
            IsCold: false,
            IsFfi: false,
            IsVarargs: false,
            IsStrictFp: false);

        var syntaxModel = new SyntaxModel(
            "Facade",
            [],
            [
                new TopLevelDeclarationModel(
                    "WriteLine",
                    DeclarationKind.Function,
                    StarkVisibility.Public,
                    new FunctionDeclarationModel(
                        Name: "WriteLine",
                        Kind: StarkFunctionKind.Fn,
                        ReturnType: "void",
                        Parameters:
                        [
                            new ParameterModel("handle", "rawptr<i8>"),
                            new ParameterModel("text", "ascii")
                        ],
                        Modifiers: Modifiers(),
                        HasBody: false,
                        PublishedOverloadKey: "(rawptr<i8[minmax]>,ascii)")),
                new TopLevelDeclarationModel(
                    "WriteLine",
                    DeclarationKind.Function,
                    StarkVisibility.Public,
                    new FunctionDeclarationModel(
                        Name: "WriteLine",
                        Kind: StarkFunctionKind.Fn,
                        ReturnType: "void",
                        Parameters:
                        [
                            new ParameterModel("handle", "rawptr<i8>"),
                            new ParameterModel("text", "unicode")
                        ],
                        Modifiers: Modifiers(),
                        HasBody: false,
                        PublishedOverloadKey: "(rawptr<i8[minmax]>,unicode)"))
            ]);

        var ascii = syntaxModel.Declarations[0];
        var unicode = syntaxModel.Declarations[1];

        Assert.Equal(
            "WriteLine#(rawptr<i8[minmax]>,ascii)",
            FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, ascii));
        Assert.Equal(
            "WriteLine#(rawptr<i8[minmax]>,unicode)",
            FunctionOverloadFacts.GetResolvedLocalName(syntaxModel, unicode));
    }


    [Fact]
    public void ManifestBackedModulesCanBeReconstructedFromStructuredTypedInterfaceSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-loader-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }
                public alias BufferView<T> = Pair<T>[];
                public fn T Identity<T>(T value);
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            Functions = [],
                            Types = [],
                            Globals = [],
                            TypeAliases = [],
                            TypedInterface = facadeModule.TypedInterface
                        }
                        : module)
                    .ToArray()
            };
            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var sourceText));
            Assert.Contains("public alias BufferView<T> = Pair<T>[];", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);

            Assert.Contains(
                importedModule.SyntaxModel.Declarations,
                static declaration => declaration.Kind == DeclarationKind.Record && declaration.Name == "Pair");
            Assert.Contains(
                importedModule.SyntaxModel.Declarations,
                static declaration => declaration.Kind == DeclarationKind.TypeAlias && declaration.Name == "BufferView");

            var identity = Assert.Single(
                importedModule.SyntaxModel.Declarations,
                static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Identity");
            Assert.NotNull(identity.Function);
            Assert.True(identity.Function!.IsGeneric);
            Assert.Equal(["T"], identity.Function.GenericParams);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesStructuredFunctionEffectFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-effects-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public strictfp hot noinline finite law f32 Precise(f32 value);
                export cold unsafe ffi fn void Sink(rawptr<i8[min max]> value);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var compilerFacts = facadeModule.CompilerFacts!;
            var precise = Assert.Single(compilerFacts.FunctionEffects, static effect => effect.QualifiedResolvedName == "Facade.Precise");
            Assert.Equal("finitelaw", precise.Kind);
            Assert.True(precise.IsPure);
            Assert.True(precise.UseFastCallingConvention);
            Assert.True(precise.IsHot);
            Assert.False(precise.IsCold);
            Assert.Equal("noinline", precise.InlinePreference);
            Assert.True(precise.IsStrictFp);

            var sink = Assert.Single(compilerFacts.FunctionEffects, static effect => effect.QualifiedResolvedName == "Facade.Sink");
            Assert.True(sink.IsFfi);
            Assert.True(sink.IsCold);
            Assert.False(sink.UseFastCallingConvention);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesStructuredAbiFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-abi-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Big {
                    i64[min max] A;
                    i64[min max] B;
                    i64[min max] C;
                }

                public fn Big Make();
                public fn i64[min max] Read(Big value);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var abiFacts = facadeModule.CompilerFacts!.AbiFunctions;
            Assert.NotNull(abiFacts);

            var make = Assert.Single(abiFacts!, static function => function.QualifiedResolvedName == "Facade.Make");
            Assert.Equal("Facade.Make", make.SymbolName);
            Assert.Equal("void", make.LlvmReturnType.Kind);
            Assert.Equal("sret", Assert.Single(make.Parameters).Kind);

            var read = Assert.Single(abiFacts, static function => function.QualifiedResolvedName == "Facade.Read");
            Assert.Equal("Facade.Read", read.SymbolName);
            var valueParameter = Assert.Single(read.Parameters);
            Assert.Equal("indirectin", valueParameter.Kind);
            Assert.Equal("rawpointer", valueParameter.LlvmType.Kind);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesStructuredLayoutFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-layout-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Padded {
                    i8[min max] Small;
                    i32[min max] Value;
                }

                public enum Token {
                    End,
                    Move { X: i32[min max], Y: i32[min max] },
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var compilerFacts = facadeModule.CompilerFacts!;
            var paddedLayout = Assert.Single(compilerFacts.ConcreteLayouts!, static layout => layout.QualifiedTypeName == "Facade.Padded");
            Assert.Equal(8, paddedLayout.SizeBytes);
            Assert.Equal(4, paddedLayout.AlignmentBytes);

            var tokenLayout = Assert.Single(compilerFacts.EnumLayouts!, static layout => layout.QualifiedTypeName == "Facade.Token");
            Assert.Equal("directtag", tokenLayout.Kind);
            Assert.Equal("$tag", tokenLayout.TagField.Name);
            Assert.Equal(["$tag", "$Move_X", "$Move_Y"], tokenLayout.OrderedFields.Select(static field => field.Name).ToArray());

            var moveVariant = Assert.Single(tokenLayout.Variants, static variant => variant.Name == "Move");
            Assert.Equal(1, moveVariant.TagValue);
            Assert.Equal("$Move_X", moveVariant.Fields[0].StorageFieldName);
            Assert.Equal("$Move_Y", moveVariant.Fields[1].StorageFieldName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesStructuredSemanticBorrowFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-semantics-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[min max] Value;
                }

                public fn retborrow Box Echo(retborrow Box value) {
                    return value;
                }

                public fn void Reset(borrow mut Box value) {
                    value.Value = 0;
                    return;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var semantics = facadeModule.CompilerFacts!.FunctionSemantics;
            Assert.NotNull(semantics);

            var echo = Assert.Single(semantics!, static summary => summary.QualifiedResolvedName == "Facade.Echo");
            Assert.NotNull(echo.MemoryEffects);
            Assert.True(echo.MemoryEffects.CapturesArgumentMemory);
            var echoParameter = Assert.Single(echo.Parameters!);
            Assert.Equal("value", echoParameter.Name);
            Assert.True(echoParameter.GuaranteedReadOnly);
            Assert.Equal(4, echoParameter.DereferenceableBytes);
            Assert.Equal(4, echoParameter.AlignmentBytes);
            Assert.Equal("return", echoParameter.CaptureKind);

            var reset = Assert.Single(semantics, static summary => summary.QualifiedResolvedName == "Facade.Reset");
            Assert.NotNull(reset.MemoryEffects);
            Assert.True(reset.MemoryEffects!.WritesArgumentMemory);
            Assert.False(reset.MemoryEffects.CapturesArgumentMemory);
            var resetParameter = Assert.Single(reset.Parameters!);
            Assert.True(resetParameter.Writes);
            Assert.Equal("none", resetParameter.CaptureKind);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesStructuredSemanticCallFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-semantic-calls-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[min max] Value;
                }

                public fn void Touch(borrow mut Box box) {
                    box.Value = 1;
                    return;
                }

                public fn void Outer(borrow mut Box box) {
                    Touch(box);
                    return;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var semantics = facadeModule.CompilerFacts!.FunctionSemantics;
            Assert.NotNull(semantics);

            var outer = Assert.Single(semantics!, static summary => summary.QualifiedResolvedName == "Facade.Outer");
            Assert.Contains("Facade.Touch", outer.CalledFunctions);
            var call = Assert.Single(outer.Calls!);
            Assert.Equal("Facade.Touch", call.CalleeName);
            Assert.True(call.MemoryEffects.WritesArgumentMemory);
            Assert.False(call.MemoryEffects.ReadsOtherMemory);
            var argument = Assert.Single(call.Arguments);
            Assert.Equal(0, argument.ArgumentIndex);
            Assert.Equal("box", argument.CallerParameterName);
            Assert.Equal("box", argument.CalleeParameterName);
            Assert.True(argument.Writes);
            Assert.Equal("none", argument.CaptureKind);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesOnlyApiVisibleGenericTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-publication-rules-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                fn T LocalIdentity<T>(T value) {
                    return value;
                }

                internal fn T InternalIdentity<T>(T value) {
                    return value;
                }

                public fn T PublicIdentity<T>(T value) {
                    return value;
                }

                public fn i32[min max] ConcreteIdentity(i32[min max] value) {
                    return value;
                }

                struct LocalBox<T> {
                    T Value;

                    fn T Echo(borrow LocalBox<T> self, T fallback) {
                        return self.Value;
                    }
                }

                internal struct InternalBox<T> {
                    T Value;

                    fn T Echo(borrow InternalBox<T> self, T fallback) {
                        return self.Value;
                    }
                }

                public struct PublicBox<T> {
                    T Value;

                    fn T Echo(borrow PublicBox<T> self, T fallback) {
                        return self.Value;
                    }
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.NotNull(facadeModule.TypedInterface);
            Assert.DoesNotContain(facadeModule.TypedInterface!.Functions, static function => function.QualifiedResolvedName == "Facade.LocalIdentity");
            var internalIdentity = Assert.Single(
                facadeModule.TypedInterface.Functions,
                static function => function.QualifiedResolvedName == "Facade.InternalIdentity");
            Assert.Equal("internal", internalIdentity.Visibility);
            Assert.False(internalIdentity.HasGenericTemplateBody);

            var publicIdentity = Assert.Single(
                facadeModule.TypedInterface.Functions,
                static function => function.QualifiedResolvedName == "Facade.PublicIdentity");
            Assert.True(publicIdentity.HasGenericTemplateBody);

            var concreteIdentity = Assert.Single(
                facadeModule.TypedInterface.Functions,
                static function => function.QualifiedResolvedName == "Facade.ConcreteIdentity");
            Assert.False(concreteIdentity.HasGenericTemplateBody);

            Assert.DoesNotContain(facadeModule.TypedInterface.Types, static type => type.QualifiedName == "Facade.LocalBox");
            var internalBox = Assert.Single(
                facadeModule.TypedInterface.Types,
                static type => type.QualifiedName == "Facade.InternalBox");
            Assert.Equal("internal", internalBox.Visibility);
            var internalEcho = Assert.Single(
                internalBox.Methods!,
                static method => method.QualifiedResolvedName == "Facade.InternalBox.Echo");
            Assert.False(internalEcho.HasGenericTemplateBody);

            var publicBox = Assert.Single(
                facadeModule.TypedInterface.Types,
                static type => type.QualifiedName == "Facade.PublicBox");
            var publicEcho = Assert.Single(
                publicBox.Methods!,
                static method => method.QualifiedResolvedName == "Facade.PublicBox.Echo");
            Assert.True(publicEcho.HasGenericTemplateBody);

            var templates = facadeModule.GenericTemplates!.Functions
                .Select(static template => template.QualifiedResolvedName)
                .ToArray();
            Assert.Contains("Facade.PublicIdentity", templates);
            Assert.Contains("Facade.PublicBox.Echo", templates);
            Assert.DoesNotContain("Facade.LocalIdentity", templates);
            Assert.DoesNotContain("Facade.InternalIdentity", templates);
            Assert.DoesNotContain("Facade.ConcreteIdentity", templates);
            Assert.DoesNotContain("Facade.LocalBox.Echo", templates);
            Assert.DoesNotContain("Facade.InternalBox.Echo", templates);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesGenericTemplateBodySections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-templates-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    fn T Echo<T>(T value) {
                        return value;
                    }
                }

                public fn T Identity<T>(T value) {
                    stack T copy = value;
                    return copy;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var templates = facadeModule.GenericTemplates!.Functions;
            var identity = Assert.Single(templates, static template => template.QualifiedResolvedName == "Facade.Identity");
            Assert.Equal("Facade.Identity", identity.QualifiedName);
            Assert.Equal("(T)", identity.OverloadKey);
            Assert.Null(identity.BodyText);
            Assert.NotNull(identity.TypedBody);
            Assert.Equal(2, identity.TopLevelStatementCount);

            var echo = Assert.Single(templates, static template => template.QualifiedResolvedName == "Facade.Box.Echo");
            Assert.Equal("Facade.Box.Echo", echo.QualifiedName);
            Assert.Equal("(T)", echo.OverloadKey);
            Assert.Null(echo.BodyText);
            Assert.NotNull(echo.TypedBody);
            Assert.Equal(1, echo.TopLevelStatementCount);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"TypedBody\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesTypedTemplateBodiesForMethodsOnGenericTypes()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-generic-type-method-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T> {
                    T Value;

                    fn T Echo(borrow Box<T> self, T fallback) {
                        return self.Value;
                    }
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var boxType = Assert.Single(facadeModule.TypedInterface!.Types, static type => type.QualifiedName == "Facade.Box");
            var echoMethod = Assert.Single(boxType.Methods!, static method => method.QualifiedResolvedName == "Facade.Box.Echo");
            Assert.Null(echoMethod.GenericParameters);
            Assert.True(echoMethod.HasGenericTemplateBody);

            var echoTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Box.Echo");
            Assert.Null(echoTemplate.BodyText);
            Assert.NotNull(echoTemplate.TypedBody);
            Assert.Equal(1, echoTemplate.TopLevelStatementCount);

            var publishedReturn = Assert.Single(echoTemplate.TypedBody!.Statements);
            Assert.Equal("return", publishedReturn.Kind);
            Assert.Equal("field-access", publishedReturn.Expression.Kind);
            var receiver = Assert.Single(publishedReturn.Expression.Arguments!);
            Assert.Equal("name", receiver.Kind);
            Assert.Equal("self", receiver.Name);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"Facade.Box.Echo\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public void PackageManifestIncludesGenericTemplateSemanticSummaries()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-semantics-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[min max] Value;
                }

                public fn void Touch<T>(borrow Box box, T tag) {
                    stack i32[min max] copy = box.Value;
                    return;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static function => function.QualifiedResolvedName == "Facade.Touch");
            Assert.NotNull(template.Semantics);
            Assert.NotNull(template.Semantics!.MemoryEffects);
            Assert.True(template.Semantics.MemoryEffects!.ReadsArgumentMemory);
            Assert.False(template.Semantics.MemoryEffects.WritesArgumentMemory);
            var boxParameter = Assert.Single(template.Semantics.Parameters!, static parameter => parameter.Name == "box");
            Assert.True(boxParameter.GuaranteedReadOnly);
            Assert.Equal(4, boxParameter.DereferenceableBytes);
            Assert.Equal(4, boxParameter.AlignmentBytes);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesGenericTemplateSemanticCallFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-semantic-calls-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32[min max] Value;
                }

                public fn void Reset(borrow mut Box box) {
                    box.Value = 0;
                    return;
                }

                public fn void Touch<T>(borrow mut Box box, T tag) {
                    Reset(box);
                    return;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static function => function.QualifiedResolvedName == "Facade.Touch");

            Assert.NotNull(template.Semantics);
            Assert.Contains("Facade.Reset", template.Semantics!.CalledFunctions);
            var call = Assert.Single(template.Semantics.Calls!);
            Assert.Equal("Facade.Reset", call.CalleeName);
            Assert.True(call.MemoryEffects.WritesArgumentMemory);
            var argument = Assert.Single(call.Arguments);
            Assert.Equal(0, argument.ArgumentIndex);
            Assert.Equal("box", argument.CallerParameterName);
            Assert.Equal("box", argument.CalleeParameterName);
            Assert.True(argument.Writes);
            Assert.Equal("none", argument.CaptureKind);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesGenericTemplateEffectiveKindsInSemanticSummaries()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-effective-kinds-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] AddTag<T>(i32[min max] left, i32[min max] right, T tag) {
                    return left + right;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static function => function.QualifiedResolvedName == "Facade.AddTag");
            Assert.NotNull(template.Semantics);
            Assert.Equal("fn", template.Semantics!.DeclaredKind);
            Assert.Equal("finitelaw", template.Semantics.EffectiveKind);
            Assert.Empty(template.Semantics.CalledFunctions);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesGroupedLocalDeclarationTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-grouped-local-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] SumPair<T>(i32[min max] value, T tag) {
                    stack i32[min max] first = value, second = value + 1;
                    return first + second;
                }

                public fn i32[min max] SumTo<T>(i32[min max] limit, T tag) {
                    stack mut i32[min max] total = 0, stop = limit;
                    for willexit (stack mut i32[min max] index = 0, max = stop; index < max; index += 1) {
                        total += index;
                    }

                    return total;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var sumPair = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.SumPair");

            Assert.Null(sumPair.BodyText);
            Assert.NotNull(sumPair.TypedBody);
            Assert.Equal(2, sumPair.TopLevelStatementCount);
            Assert.Equal(3, sumPair.TypedBody!.Statements.Count);
            Assert.Collection(
                sumPair.TypedBody.Statements,
                statement =>
                {
                    Assert.Equal("local-variable", statement.Kind);
                    Assert.Equal("first", statement.Name);
                },
                statement =>
                {
                    Assert.Equal("local-variable", statement.Kind);
                    Assert.Equal("second", statement.Name);
                },
                statement =>
                {
                    Assert.Equal("return", statement.Kind);
                    Assert.NotNull(statement.Expression);
                });

            var sumTo = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.SumTo");
            Assert.Null(sumTo.BodyText);
            Assert.NotNull(sumTo.TypedBody);
            Assert.Equal(3, sumTo.TopLevelStatementCount);
            Assert.Equal(4, sumTo.TypedBody!.Statements.Count);
            Assert.Equal("total", sumTo.TypedBody.Statements[0].Name);
            Assert.Equal("stop", sumTo.TypedBody.Statements[1].Name);
            var loop = sumTo.TypedBody.Statements[2];
            Assert.Equal("for", loop.Kind);
            Assert.NotNull(loop.InitializerStatements);
            Assert.Equal(2, loop.InitializerStatements!.Count);
            Assert.Equal("index", loop.InitializerStatements[0].Name);
            Assert.Equal("max", loop.InitializerStatements[1].Name);
            Assert.Equal("return", sumTo.TypedBody.Statements[3].Kind);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"TypedBody\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesDiscardedExpressionStatementTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-discarded-expression-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn void Observe<T>(i32[min max] value, T tag) {
                    value + 1;
                    return;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Observe");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.Equal(2, template.TopLevelStatementCount);
            Assert.Equal(2, template.TypedBody!.Statements.Count);
            Assert.Equal("expression", template.TypedBody.Statements[0].Kind);
            Assert.Equal("binary", template.TypedBody.Statements[0].Expression.Kind);
            Assert.Equal("return", template.TypedBody.Statements[1].Kind);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"TypedBody\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesUninitializedLocalDeclarationTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-uninitialized-local-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Observe<T>(i32[min max] value, T tag) {
                    stack mut i32[min max] current, next = value + 1;
                    current = value;
                    return current + next;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Observe");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.Equal(3, template.TopLevelStatementCount);
            Assert.Equal(4, template.TypedBody!.Statements.Count);
            Assert.Equal("local-variable", template.TypedBody.Statements[0].Kind);
            Assert.Null(template.TypedBody.Statements[0].Expression);
            Assert.Equal("current", template.TypedBody.Statements[0].Name);
            Assert.Equal("local-variable", template.TypedBody.Statements[1].Kind);
            Assert.Equal("binary", template.TypedBody.Statements[1].Expression.Kind);
            Assert.Equal("next", template.TypedBody.Statements[1].Name);
            Assert.Equal("assignment", template.TypedBody.Statements[2].Kind);
            Assert.Equal("return", template.TypedBody.Statements[3].Kind);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"TypedBody\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesObjectInitializerLocalDeclarationTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-object-initializer-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair(i32[min max] First, i32[min max] Second) { }

                public fn i32[min max] Observe<T>(i32[min max] value, T tag) {
                    stack Pair pair = { First = value, Second = value + 1 };
                    return pair.First + pair.Second;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Observe");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.Equal(2, template.TopLevelStatementCount);
            Assert.Equal(2, template.TypedBody!.Statements.Count);
            Assert.Equal("local-variable", template.TypedBody.Statements[0].Kind);
            Assert.Equal("pair", template.TypedBody.Statements[0].Name);
            Assert.Equal("object-initializer", template.TypedBody.Statements[0].Expression.Kind);
            Assert.Equal(["First", "Second"], template.TypedBody.Statements[0].Expression.MemberNames);
            Assert.Equal(2, template.TypedBody.Statements[0].Expression.Arguments!.Count);
            Assert.Equal("return", template.TypedBody.Statements[1].Kind);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"object-initializer\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesEmptyBlockAndOpenEndedLoopTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-empty-block-loop-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn void NoOp<T>(T tag) { }

                public fn i32[min max] KeepValue<T>(i32[min max] value, T tag) {
                    ;
                    return value;
                }

                public fn i32[min max] NestedScope<T>(i32[min max] value, T tag) {
                    {
                        ;
                    }

                    return value;
                }

                public fn i32[min max] CountTo<T>(i32[min max] count, T tag) {
                    stack mut i32[min max] index = 0;
                    for willexit (;;) {
                        if (index == count) {
                            break;
                        }

                        index = index + 1;
                    }

                    return index;
                }

                public fn i32[min max] EmptySwitch<T>(i32[min max] value, T tag) {
                    switch (value) {
                        case 0:
                    }

                    return value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var noOp = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.NoOp");
            Assert.Null(noOp.BodyText);
            Assert.NotNull(noOp.TypedBody);
            Assert.Empty(noOp.TypedBody!.Statements);

            var keepValue = Assert.Single(facadeModule.GenericTemplates.Functions, static template => template.QualifiedResolvedName == "Facade.KeepValue");
            Assert.Equal(["empty", "return"], keepValue.TypedBody!.Statements.Select(static statement => statement.Kind));

            var nestedScope = Assert.Single(facadeModule.GenericTemplates.Functions, static template => template.QualifiedResolvedName == "Facade.NestedScope");
            Assert.Equal("block", nestedScope.TypedBody!.Statements[0].Kind);
            Assert.NotNull(nestedScope.TypedBody.Statements[0].BodyStatements);
            Assert.Equal("empty", Assert.Single(nestedScope.TypedBody.Statements[0].BodyStatements!).Kind);

            var countTo = Assert.Single(facadeModule.GenericTemplates.Functions, static template => template.QualifiedResolvedName == "Facade.CountTo");
            var loop = Assert.Single(countTo.TypedBody!.Statements, static statement => statement.Kind == "for");
            Assert.Null(loop.Expression);
            Assert.NotNull(loop.BodyStatements);

            var emptySwitch = Assert.Single(facadeModule.GenericTemplates.Functions, static template => template.QualifiedResolvedName == "Facade.EmptySwitch");
            var switchStatement = Assert.Single(emptySwitch.TypedBody!.Statements, static statement => statement.Kind == "switch");
            Assert.NotNull(switchStatement.SwitchCases);
            Assert.Single(switchStatement.SwitchCases!);
            Assert.Empty(Assert.Single(switchStatement.SwitchCases!).Statements ?? []);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"empty\"", json, StringComparison.Ordinal);
            Assert.Contains("\"block\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesNestedInitializerObjectCreationTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-nested-object-creation-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Inner<T> {
                    T Value;
                }

                public struct Outer<T> {
                    Inner<T> Item;
                    i32[min max][2] Values;
                }

                public fn Outer<T> Wrap<T>(T value, T tag) {
                    return new Outer<T>() {
                        Item = { Value = value },
                        Values = { 7, 9 }
                    };
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Wrap");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            var publishedReturn = Assert.Single(template.TypedBody!.Statements);
            Assert.Equal("return", publishedReturn.Kind);
            Assert.Equal("object-creation", publishedReturn.Expression.Kind);
            Assert.Equal(2, publishedReturn.Expression.Arguments!.Count);
            Assert.Equal("object-initializer", publishedReturn.Expression.Arguments[0].Kind);
            Assert.Equal(["Value"], publishedReturn.Expression.Arguments[0].MemberNames);
            Assert.Equal("array-initializer", publishedReturn.Expression.Arguments[1].Kind);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"object-initializer\"", json, StringComparison.Ordinal);
            Assert.Contains("\"array-initializer\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesAssignmentExpressionTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-assignment-expression-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Observe<T>(i32[min max] value, T tag) {
                    stack mut i32[min max] current = 1;
                    return current += value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Observe");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.Equal(2, template.TopLevelStatementCount);
            Assert.Equal(2, template.TypedBody!.Statements.Count);
            Assert.Equal("local-variable", template.TypedBody.Statements[0].Kind);
            Assert.Equal("return", template.TypedBody.Statements[1].Kind);
            Assert.Equal("assignment", template.TypedBody.Statements[1].Expression.Kind);
            Assert.Equal("+=", template.TypedBody.Statements[1].Expression.AssignmentOperator);
            var assignmentTarget = Assert.IsType<StarkPackageTypedTemplateExpressionManifest>(template.TypedBody.Statements[1].Expression.TargetExpression);
            Assert.Equal("name", assignmentTarget.Kind);
            Assert.Equal("current", assignmentTarget.Name);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"assignment\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesRawPointerDereferenceTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-raw-pointer-deref-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public unsafe fn i32[min max] Observe<T>(rawmutptr<i32[min max]> ptr, i32[min max] value, T tag) {
                    stack mut i32[min max] copy = *ptr;
                    return *ptr += copy + value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Observe");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.Equal(2, template.TopLevelStatementCount);
            Assert.Equal(2, template.TypedBody!.Statements.Count);
            Assert.Equal("local-variable", template.TypedBody.Statements[0].Kind);
            Assert.Equal("unary", template.TypedBody.Statements[0].Expression.Kind);
            Assert.Equal("*", template.TypedBody.Statements[0].Expression.Name);
            Assert.Equal("return", template.TypedBody.Statements[1].Kind);
            Assert.Equal("assignment", template.TypedBody.Statements[1].Expression.Kind);
            var assignmentTarget = Assert.IsType<StarkPackageTypedTemplateExpressionManifest>(template.TypedBody.Statements[1].Expression.TargetExpression);
            Assert.Equal("unary", assignmentTarget.Kind);
            Assert.Equal("*", assignmentTarget.Name);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"unary\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesProjectedRawPointerDereferenceTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-projected-raw-pointer-deref-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(i32[min max] First, i32[min max][4] Values) { }

                public unsafe fn rawmutptr<Buffer> Pick<T>(rawmutptr<Buffer> ptr, T tag) {
                    return ptr;
                }

                public unsafe fn i32[min max] Observe<T>(rawmutptr<Buffer> ptr, i32[min max] slot, i32[min max] value, T tag) {
                    (*ptr).First += value;
                    return (*Pick(ptr, tag)).Values[slot] = (*ptr).First + value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Observe");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.Equal(2, template.TopLevelStatementCount);
            Assert.Equal(2, template.TypedBody!.Statements.Count);

            var compoundStatement = template.TypedBody.Statements[0];
            Assert.Equal("assignment", compoundStatement.Kind);
            Assert.Equal("+=", compoundStatement.AssignmentOperator);
            var compoundTarget = Assert.IsType<StarkPackageTypedTemplateExpressionManifest>(compoundStatement.TargetExpression);
            Assert.Equal("field-access", compoundTarget.Kind);
            var compoundRoot = Assert.Single(compoundTarget.Arguments!);
            Assert.Equal("unary", compoundRoot.Kind);
            Assert.Equal("*", compoundRoot.Name);

            var returnStatement = template.TypedBody.Statements[1];
            Assert.Equal("return", returnStatement.Kind);
            Assert.Equal("assignment", returnStatement.Expression.Kind);
            Assert.Equal("=", returnStatement.Expression.AssignmentOperator);
            var projectedTarget = Assert.IsType<StarkPackageTypedTemplateExpressionManifest>(returnStatement.Expression.TargetExpression);
            Assert.Equal("index-access", projectedTarget.Kind);
            Assert.Equal(2, projectedTarget.Arguments!.Count);
            var fieldTarget = projectedTarget.Arguments[0];
            Assert.Equal("field-access", fieldTarget.Kind);
            var unaryTarget = Assert.Single(fieldTarget.Arguments!);
            Assert.Equal("unary", unaryTarget.Kind);
            Assert.Equal("*", unaryTarget.Name);
            var callTarget = Assert.Single(unaryTarget.Arguments!);
            Assert.Equal("direct-call", callTarget.Kind);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"field-access\"", json, StringComparison.Ordinal);
            Assert.Contains("\"index-access\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesAddressOfTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-address-of-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(i32[min max] First, i32[min max][4] Values) { }

                public unsafe fn i32[min max] Observe<T>(i32[min max] value, T tag) {
                    stack mut i32[min max][4] data = { 1, 2, 3, 4 };
                    stack mut Buffer buffer = { First = value, Values = data };
                    stack rawmutptr<i32[min max]> firstPtr = &buffer.First;
                    stack rawmutptr<i32[min max]> slotPtr = &buffer.Values[2];
                    stack rawmutptr<i32[min max]> aliasPtr = &*slotPtr;
                    return *aliasPtr = *firstPtr + value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Observe");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.Equal(6, template.TopLevelStatementCount);
            Assert.Equal(6, template.TypedBody!.Statements.Count);

            var firstPointerInitializer = template.TypedBody.Statements[2];
            Assert.Equal("local-variable", firstPointerInitializer.Kind);
            Assert.Equal("unary", firstPointerInitializer.Expression.Kind);
            Assert.Equal("&", firstPointerInitializer.Expression.Name);
            var firstPointerTarget = Assert.Single(firstPointerInitializer.Expression.Arguments!);
            Assert.Equal("field-access", firstPointerTarget.Kind);

            var slotPointerInitializer = template.TypedBody.Statements[3];
            Assert.Equal("local-variable", slotPointerInitializer.Kind);
            Assert.Equal("unary", slotPointerInitializer.Expression.Kind);
            Assert.Equal("&", slotPointerInitializer.Expression.Name);
            var slotPointerTarget = Assert.Single(slotPointerInitializer.Expression.Arguments!);
            Assert.Equal("index-access", slotPointerTarget.Kind);

            var aliasPointerInitializer = template.TypedBody.Statements[4];
            Assert.Equal("local-variable", aliasPointerInitializer.Kind);
            Assert.Equal("unary", aliasPointerInitializer.Expression.Kind);
            Assert.Equal("&", aliasPointerInitializer.Expression.Name);
            var aliasPointerTarget = Assert.Single(aliasPointerInitializer.Expression.Arguments!);
            Assert.Equal("unary", aliasPointerTarget.Kind);
            Assert.Equal("*", aliasPointerTarget.Name);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\\u0026", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesPowerTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-power-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Observe<T>(i32[min max] value, i32[min max] exponent, T tag) {
                    return value ** exponent;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Observe");

            Assert.Null(template.BodyText);
            Assert.NotNull(template.TypedBody);
            Assert.Equal(1, template.TopLevelStatementCount);
            var returnStatement = Assert.Single(template.TypedBody!.Statements);
            Assert.Equal("return", returnStatement.Kind);
            Assert.Equal("binary", returnStatement.Expression.Kind);
            Assert.Equal("**", returnStatement.Expression.Name);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"**\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPublishesComparisonChainTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-comparison-chain-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                fn i32[min max] Next() {
                    return 1;
                }

                public fn bool ObserveOrdered<T>(T tag) {
                    return 0 < Next() < 3;
                }

                public fn bool ObserveEquality<T>(T tag) {
                    return 1 == Next() == 1;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var ordered = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.ObserveOrdered");
            var equality = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.ObserveEquality");

            Assert.Null(ordered.BodyText);
            Assert.NotNull(ordered.TypedBody);
            var orderedReturn = Assert.Single(ordered.TypedBody!.Statements);
            Assert.Equal("return", orderedReturn.Kind);
            Assert.Equal("comparison-chain", orderedReturn.Expression.Kind);
            Assert.Equal(["<", "<"], orderedReturn.Expression.OperatorNames);
            Assert.Equal(3, orderedReturn.Expression.Arguments!.Count);

            Assert.Null(equality.BodyText);
            Assert.NotNull(equality.TypedBody);
            var equalityReturn = Assert.Single(equality.TypedBody!.Statements);
            Assert.Equal("return", equalityReturn.Kind);
            Assert.Equal("comparison-chain", equalityReturn.Expression.Kind);
            Assert.Equal(["==", "=="], equalityReturn.Expression.OperatorNames);
            Assert.Equal(3, equalityReturn.Expression.Arguments!.Count);

            var json = manifest.ToJson();
            Assert.DoesNotContain("\"BodyText\"", json, StringComparison.Ordinal);
            Assert.Contains("\"comparison-chain\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageImageSyntaxModelCarriesFunctionModifiersFromTypedInterfaceWhenCompilerFactsAreMissing()
    {
        var module = new StarkPackageModuleManifest(
            ModuleName: "Facade",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions:
                [
                    new StarkPackageTypedFunctionManifest(
                        Name: "Choose",
                        QualifiedName: "Facade.Choose",
                        Visibility: "public",
                        SymbolName: "Facade.Choose",
                        Kind: "fn",
                        ReturnType: SignedIntegerTypeReference(32),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest("left", SignedIntegerTypeReference(32)),
                            new StarkPackageTypedParameterManifest("right", SignedIntegerTypeReference(32))
                        ],
                        IsFfi: false,
                        IsStrictFp: true,
                        UseFastCallingConvention: true,
                        QualifiedResolvedName: "Facade.Choose",
                        PublishedOverloadKey: "(i32, i32)",
                        IsHot: true,
                        InlinePreference: "noinline",
                        HasExplicitInlinePreference: true)
                ],
                Types:
                [
                    new StarkPackageTypedTypeManifest(
                        Name: "Box",
                        QualifiedName: "Facade.Box",
                        Visibility: "public",
                        Kind: "struct",
                        Fields:
                        [
                            new StarkPackageTypedFieldManifest(
                                "Value",
                                SignedIntegerTypeReference(32))
                        ],
                        Methods:
                        [
                            new StarkPackageTypedMethodManifest(
                                Name: "Measure",
                                QualifiedName: "Facade.Box.Measure",
                                SymbolName: "Facade.Box.Measure",
                                Kind: "fn",
                                ReturnType: SignedIntegerTypeReference(32),
                                Parameters:
                                [
                                    new StarkPackageTypedParameterManifest(
                                        "delta",
                                        SignedIntegerTypeReference(32))
                                ],
                                IsFfi: false,
                                IsStrictFp: false,
                                UseFastCallingConvention: true,
                                QualifiedResolvedName: "Facade.Box.Measure",
                                PublishedOverloadKey: "(i32)",
                                IsCold: true,
                                InlinePreference: "inlinehint",
                                HasExplicitInlinePreference: true)
                        ]),
                ],
                Globals: []));
        var resolvedModule = new ResolvedPackageModule(
            ManifestPath: "/tmp/facade.starkpkg.json",
            LibraryPath: "/tmp/libFacade.a",
            Manifest: new StarkPackageManifest("Facade", "libFacade.a", [module]),
            Module: module);

        Assert.True(PackageImageLoader.TryBuildModuleSyntaxModel(resolvedModule, out var syntaxModel));

        var chooseDeclaration = Assert.Single(
            syntaxModel.Declarations,
            static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Choose");
        Assert.NotNull(chooseDeclaration.Function);
        Assert.True(chooseDeclaration.Function!.Modifiers.IsStrictFp);
        Assert.True(chooseDeclaration.Function.Modifiers.IsHot);
        Assert.False(chooseDeclaration.Function.Modifiers.IsCold);
        Assert.Equal(InlinePreference.NoInline, chooseDeclaration.Function.Modifiers.InlinePreference);
        Assert.True(chooseDeclaration.Function.Modifiers.HasExplicitInlinePreference);

        var measureDeclaration = Assert.Single(
            syntaxModel.Declarations,
            static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Box.Measure");
        Assert.NotNull(measureDeclaration.Function);
        Assert.False(measureDeclaration.Function!.Modifiers.IsStrictFp);
        Assert.False(measureDeclaration.Function.Modifiers.IsHot);
        Assert.True(measureDeclaration.Function.Modifiers.IsCold);
        Assert.Equal(InlinePreference.InlineHint, measureDeclaration.Function.Modifiers.InlinePreference);
        Assert.True(measureDeclaration.Function.Modifiers.HasExplicitInlinePreference);

        Assert.True(PackageImageLoader.TryBuildModuleSource(resolvedModule, out var sourceText));
        Assert.Contains(StrictIntegerSource("public strictfp hot noinline fn i32 Choose(i32 left, i32 right);"), sourceText, StringComparison.Ordinal);
        Assert.Contains(StrictIntegerSource("cold inlinehint fn i32 Measure(i32 delta);"), sourceText, StringComparison.Ordinal);
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateObjectCreationFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-object-creations-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn Pair<T> MakePair<T>(T value) {
                    return new Pair<T>(value);
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.MakePair");
            var objectCreation = Assert.Single(template.ObjectCreations!);
            Assert.Equal("named", objectCreation.CreatedType.Kind);
            Assert.Equal("Facade.Pair", objectCreation.CreatedType.Name);
            Assert.NotNull(objectCreation.Constructor);
            Assert.Contains("Pair", objectCreation.Constructor!.TypeName, StringComparison.Ordinal);
            Assert.True(objectCreation.Constructor.IsPrimaryShape);
            var parameter = Assert.Single(objectCreation.Constructor.Parameters);
            Assert.Equal("Value", parameter.Name);
            Assert.Equal("named", parameter.Type.Kind);
            Assert.Equal("T", parameter.Type.Name);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTargetTypedDefaultObjectCreationFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-target-typed-object-creations-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T> {
                    T Value;
                }

                public fn Box<T> MakeDefault<T>() {
                    return new();
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.MakeDefault");
            var objectCreation = Assert.Single(template.ObjectCreations!);

            Assert.Equal("named", objectCreation.CreatedType.Kind);
            Assert.Equal("Facade.Box", objectCreation.CreatedType.Name);
            Assert.Null(objectCreation.Constructor);
            Assert.Null(objectCreation.InitializerMembers);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateObjectInitializerMemberFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-object-initializers-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Pair<T> {
                    T Value;
                    i32[min max] Count;
                }

                public fn Pair<T> MakePair<T>(T value) {
                    return new Pair<T>() { Value = value, Count = 1 };
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.MakePair");
            var objectCreation = Assert.Single(template.ObjectCreations!);

            Assert.Null(objectCreation.Constructor);
            Assert.NotNull(objectCreation.InitializerMembers);
            Assert.Equal(2, objectCreation.InitializerMembers!.Count);

            var valueMember = objectCreation.InitializerMembers[0];
            Assert.Equal("Value", valueMember.FieldName);
            Assert.Equal(0, valueMember.FieldIndex);
            Assert.Equal("named", valueMember.FieldType.Kind);
            Assert.Equal("T", valueMember.FieldType.Name);

            var countMember = objectCreation.InitializerMembers[1];
            Assert.Equal("Count", countMember.FieldName);
            Assert.Equal(1, countMember.FieldIndex);
            Assert.Equal("integer", countMember.FieldType.Kind);
            Assert.Equal(32, countMember.FieldType.BitWidth);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateLocalDeclarationFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-locals-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    stack T copy = value;
                    const one = 1;
                    return copy;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Identity");

            Assert.NotNull(template.LocalDeclarations);
            Assert.Equal(2, template.LocalDeclarations!.Count);

            var localVariable = template.LocalDeclarations[0];
            Assert.Equal("var", localVariable.Kind);
            Assert.Equal("named", localVariable.Type.Kind);
            Assert.Equal("T", localVariable.Type.Name);

            var localConstant = template.LocalDeclarations[1];
            Assert.Equal("const", localConstant.Kind);
            Assert.Equal("integer", localConstant.Type.Kind);
            Assert.Equal(8, localConstant.Type.BitWidth);
            Assert.Equal("1", localConstant.Type.RangeMin);
            Assert.Equal("1", localConstant.Type.RangeMax);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesFirstTypedGenericTemplateBodySubset()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-typed-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    stack T copy = value;
                    return copy;
                }

                public fn T ConstIdentity<T>(T value) {
                    const T copy = value;
                    return copy;
                }

                public fn T Forward<T>(T value) {
                    return Identity(value);
                }

                public fn T Relay<T>(T value) {
                    stack T copy = value;
                    stack T echoed = Identity(copy);
                    return echoed;
                }

                public fn i32[min max] TruncateTyped<T>(f32 value, T tag) {
                    return (i32[min max])value;
                }

                public fn i32[min max] AddViaAssign<T>(T tag, i32[min max] left, i32[min max] right) {
                    stack mut i32[min max] sum = left;
                    sum = sum + right;
                    return sum;
                }

                public fn i32[min max] ChooseBranch<T>(bool takeLeft, i32[min max] left, i32[min max] right, T tag) {
                    stack mut i32[min max] result = 0;
                    if (takeLeft) {
                        result = left;
                    } else {
                        result = right;
                    }
                    return result;
                }

                public fn i32[min max] SumTo<T>(i32[min max] count, T tag) {
                    stack mut i32[min max] index = 0;
                    stack mut i32[min max] sum = 0;
                    while willexit (index < count) {
                        sum = sum + index;
                        index = index + 1;
                    }
                    return sum;
                }

                public fn i32[min max] SumFor<T>(i32[min max] count, T tag) {
                    stack mut i32[min max] sum = 0;
                    for willexit (stack mut i32[min max] index = 0; index < count; index = index + 1) {
                        sum = sum + index;
                    }
                    return sum;
                }

                public fn i32[min max] SumForControl<T>(i32[min max] count, i32[min max] stopAt, T tag) {
                    stack mut i32[min max] sum = 0;
                    for willexit (stack mut i32[min max] index = 0; index < count; index = index + 1) {
                        if (index < 2) {
                            continue;
                        }
                        if (index == stopAt) {
                            break;
                        }
                        sum = sum + index;
                    }
                    return sum;
                }

                public fn i8[min max] One<T>(T tag) {
                    return 1;
                }

                public fn bool NegateFlag<T>(T tag, bool flag) {
                    return !flag;
                }

                public fn i32[min max] AddTagged<T>(T tag, i32[min max] left, i32[min max] right) {
                    return left + right;
                }

                public fn bool Both<T>(T tag, bool left, bool right) {
                    return left && right;
                }

                public struct Box<T> {
                    T Value;
                }

                public fn T ReadValue<T>(Box<T> box, T fallback) {
                    return box.Value;
                }

                public record EchoBox(i32[min max] Dummy) {
                    fn i32[min max] Echo(borrow EchoBox self, i32[min max] value) {
                        return value;
                    }
                }

                public fn EchoBox MakeEchoBox(i32[min max] dummy) {
                    return new EchoBox(dummy);
                }

                public fn i32[min max] CallEcho<T>(EchoBox box, i32[min max] value, T tag) {
                    return box.Echo(value);
                }

                public struct EchoHolder {
                    EchoBox Box;
                }

                public fn i32[min max] CallHeldEcho<T>(EchoHolder holder, i32[min max] value, T tag) {
                    return holder.Box.Echo(value);
                }

                public fn i32[min max] CallIndexedEcho<T>(EchoBox[] boxes, i32[min max] index, i32[min max] value, T tag) {
                    return boxes[index].Echo(value);
                }

                public fn i32[min max] CallMadeEcho<T>(i32[min max] value, T tag) {
                    return MakeEchoBox(1).Echo(value);
                }

                public struct IntBox {
                    i32[min max] Value;
                }

                public fn IntBox MakeIntBox(i32[min max] value) {
                    return new IntBox() { Value = value };
                }

                public fn i32[min max] ReadMadeValue<T>(i32[min max] value, T tag) {
                    return MakeIntBox(value).Value;
                }

                public fn i32[min max] CallConstructedEcho<T>(i32[min max] value, T tag) {
                    return new EchoBox(1).Echo(value);
                }

                public fn i32[min max] ReadConstructedValue<T>(i32[min max] value, T tag) {
                    return new IntBox() { Value = value }.Value;
                }

                public fn i32[min max] ChooseBoxValue<T>(bool takeLeft, IntBox left, IntBox right, T tag) {
                    return (takeLeft ? left : right).Value;
                }

                public fn i32[min max] ChooseEcho<T>(bool takeLeft, EchoBox left, EchoBox right, i32[min max] value, T tag) {
                    return (takeLeft ? left : right).Echo(value);
                }

                public fn i32[min max] ReadSliceAt<T>(i32[min max][] view, i32[min max] index, T tag) {
                    return view[index];
                }

                public fn ascii SliceAsciiWindow<T>(ascii text, i32[min max] start, i32[min max] length, T tag) {
                    return text[start, length];
                }

                public struct SliceBox<T> {
                    i32[min max][] Values;
                }

                public fn i32[min max] ReadBoxSliceAt<T>(SliceBox<T> box, i32[min max] index, T tag) {
                    return box.Values[index];
                }

                public struct Counted<T> {
                    T Value;
                    i32[min max] Count;
                }

                public fn i32[min max] ReadIndexedCount<T>(Counted<T>[] pairs, i32[min max] index, T tag) {
                    return pairs[index].Count;
                }

                public record ResetBox(i32[min max] Value) {
                    fn void Reset(borrow mut ResetBox self) {
                        self.Value = 0;
                    }
                }

                public fn void ResetValue(borrow mut ResetBox box) {
                    box.Value = 0;
                }

                public fn void ForwardReset<T>(borrow mut ResetBox box, T tag) {
                    ResetValue(box);
                }

                public fn void ForwardMethodReset<T>(borrow mut ResetBox box, T tag) {
                    box.Reset();
                }

                public fn void GuardedReset<T>(bool shouldStop, borrow mut ResetBox box, T tag) {
                    if (shouldStop) {
                        return;
                    }
                    ResetValue(box);
                    return;
                }

                public record WrapBox<T>(T Value) { }

                public fn WrapBox<T> Wrap<T>(T value, WrapBox<T> fallback) {
                    return new WrapBox<T>(value);
                }

                public enum Option<T> {
                    None,
                    Some(T),
                }

                public fn Option<T> WrapOption<T>(T value) {
                    return Option<T>.Some(value);
                }

                public fn Option<T> EmptyLike<T>(T value) {
                    return Option<T>.None;
                }

                public enum Boxed<T> {
                    Value { Data: T, Tag: i32[min max] },
                }

                public fn Boxed<T> WrapNamed<T>(T value, i32[min max] tag) {
                    return Boxed<T>.Value { Data: value, Tag: tag };
                }

                public fn Boxed<T> WrapNamedConst<T>(T value) {
                    return Boxed<T>.Value { Data: value, Tag: 1 };
                }

                public fn T Choose<T>(bool takeLeft, T left, T right) {
                    return takeLeft ? left : right;
                }

                public fn i32[min max] MinTagged<T>(T tag, i32[min max] left, i32[min max] right) {
                    return left < right ? left : right;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var identity = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Identity");
            Assert.NotNull(identity.TypedBody);
            Assert.Equal(2, identity.TypedBody!.Statements.Count);

            var identityLocal = identity.TypedBody.Statements[0];
            Assert.Equal("local-variable", identityLocal.Kind);
            Assert.Equal("copy", identityLocal.Name);
            Assert.Equal("stack", identityLocal.StorageClass);
            Assert.False(identityLocal.IsMutable);
            Assert.NotNull(identityLocal.Type);
            Assert.Equal("named", identityLocal.Type!.Kind);
            Assert.Equal("T", identityLocal.Type.Name);
            Assert.Equal("name", identityLocal.Expression.Kind);
            Assert.Equal("value", identityLocal.Expression.Name);

            var identityReturn = identity.TypedBody.Statements[1];
            Assert.Equal("return", identityReturn.Kind);
            Assert.Equal("name", identityReturn.Expression.Kind);
            Assert.Equal("copy", identityReturn.Expression.Name);

            var constIdentity = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ConstIdentity");
            Assert.NotNull(constIdentity.TypedBody);
            Assert.Equal(2, constIdentity.TypedBody!.Statements.Count);

            var constIdentityLocal = constIdentity.TypedBody.Statements[0];
            Assert.Equal("local-variable", constIdentityLocal.Kind);
            Assert.Equal("copy", constIdentityLocal.Name);
            Assert.Equal("local", constIdentityLocal.StorageClass);
            Assert.False(constIdentityLocal.IsMutable);
            Assert.True(constIdentityLocal.IsConstant);
            Assert.NotNull(constIdentityLocal.Type);
            Assert.Equal("named", constIdentityLocal.Type!.Kind);
            Assert.Equal("T", constIdentityLocal.Type.Name);
            Assert.Equal("name", constIdentityLocal.Expression.Kind);
            Assert.Equal("value", constIdentityLocal.Expression.Name);

            var constIdentityReturn = constIdentity.TypedBody.Statements[1];
            Assert.Equal("return", constIdentityReturn.Kind);
            Assert.Equal("name", constIdentityReturn.Expression.Kind);
            Assert.Equal("copy", constIdentityReturn.Expression.Name);

            var forward = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.Forward");
            Assert.NotNull(forward.TypedBody);
            var forwardReturn = Assert.Single(forward.TypedBody!.Statements);
            Assert.Equal("return", forwardReturn.Kind);
            Assert.Equal("direct-call", forwardReturn.Expression.Kind);
            Assert.Equal(0, forwardReturn.Expression.Ordinal);
            var forwardArgument = Assert.Single(forwardReturn.Expression.Arguments!);
            Assert.Equal("name", forwardArgument.Kind);
            Assert.Equal("value", forwardArgument.Name);

            var relay = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.Relay");
            Assert.NotNull(relay.TypedBody);
            Assert.Equal(3, relay.TypedBody!.Statements.Count);

            var relayCopy = relay.TypedBody.Statements[0];
            Assert.Equal("local-variable", relayCopy.Kind);
            Assert.Equal("copy", relayCopy.Name);
            Assert.Equal("name", relayCopy.Expression.Kind);
            Assert.Equal("value", relayCopy.Expression.Name);

            var relayEchoed = relay.TypedBody.Statements[1];
            Assert.Equal("local-variable", relayEchoed.Kind);
            Assert.Equal("echoed", relayEchoed.Name);
            Assert.Equal("direct-call", relayEchoed.Expression.Kind);
            Assert.Equal(0, relayEchoed.Expression.Ordinal);
            var relayCallArgument = Assert.Single(relayEchoed.Expression.Arguments!);
            Assert.Equal("name", relayCallArgument.Kind);
            Assert.Equal("copy", relayCallArgument.Name);

            var relayReturn = relay.TypedBody.Statements[2];
            Assert.Equal("return", relayReturn.Kind);
            Assert.Equal("name", relayReturn.Expression.Kind);
            Assert.Equal("echoed", relayReturn.Expression.Name);

            var truncateTyped = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.TruncateTyped");
            Assert.NotNull(truncateTyped.TypedBody);
            var truncateTypedReturn = Assert.Single(truncateTyped.TypedBody!.Statements);
            Assert.Equal("return", truncateTypedReturn.Kind);
            Assert.Equal("conversion", truncateTypedReturn.Expression.Kind);
            var truncateTypedArgument = Assert.Single(truncateTypedReturn.Expression.Arguments!);
            Assert.Equal("name", truncateTypedArgument.Kind);
            Assert.Equal("value", truncateTypedArgument.Name);
            var truncateTypedTargetType = Assert.IsType<StarkPackageTypeReference>(truncateTypedReturn.Expression.Type);
            Assert.Equal("integer", truncateTypedTargetType.Kind);
            Assert.Equal(32, truncateTypedTargetType.BitWidth);

            var addViaAssign = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.AddViaAssign");
            Assert.NotNull(addViaAssign.TypedBody);
            Assert.Equal(3, addViaAssign.TypedBody!.Statements.Count);

            var addViaAssignLocal = addViaAssign.TypedBody.Statements[0];
            Assert.Equal("local-variable", addViaAssignLocal.Kind);
            Assert.Equal("sum", addViaAssignLocal.Name);
            Assert.Equal("stack", addViaAssignLocal.StorageClass);
            Assert.True(addViaAssignLocal.IsMutable);
            Assert.Equal("name", addViaAssignLocal.Expression.Kind);
            Assert.Equal("left", addViaAssignLocal.Expression.Name);

            var addViaAssignAssignment = addViaAssign.TypedBody.Statements[1];
            Assert.Equal("assignment", addViaAssignAssignment.Kind);
            Assert.Equal("sum", addViaAssignAssignment.Name);
            Assert.Equal("binary", addViaAssignAssignment.Expression.Kind);
            Assert.Equal("+", addViaAssignAssignment.Expression.Name);
            Assert.Equal(2, addViaAssignAssignment.Expression.Arguments!.Count);
            Assert.Equal("name", addViaAssignAssignment.Expression.Arguments[0].Kind);
            Assert.Equal("sum", addViaAssignAssignment.Expression.Arguments[0].Name);
            Assert.Equal("name", addViaAssignAssignment.Expression.Arguments[1].Kind);
            Assert.Equal("right", addViaAssignAssignment.Expression.Arguments[1].Name);

            var addViaAssignReturn = addViaAssign.TypedBody.Statements[2];
            Assert.Equal("return", addViaAssignReturn.Kind);
            Assert.Equal("name", addViaAssignReturn.Expression.Kind);
            Assert.Equal("sum", addViaAssignReturn.Expression.Name);

            var chooseBranch = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ChooseBranch");
            Assert.NotNull(chooseBranch.TypedBody);
            Assert.Equal(3, chooseBranch.TypedBody!.Statements.Count);

            var chooseBranchLocal = chooseBranch.TypedBody.Statements[0];
            Assert.Equal("local-variable", chooseBranchLocal.Kind);
            Assert.Equal("result", chooseBranchLocal.Name);
            Assert.True(chooseBranchLocal.IsMutable);
            Assert.Equal("literal", chooseBranchLocal.Expression.Kind);

            var chooseBranchIf = chooseBranch.TypedBody.Statements[1];
            Assert.Equal("if", chooseBranchIf.Kind);
            Assert.Equal("name", chooseBranchIf.Expression.Kind);
            Assert.Equal("takeLeft", chooseBranchIf.Expression.Name);
            var chooseBranchThen = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(chooseBranchIf.ThenStatements);
            var chooseBranchElse = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(chooseBranchIf.ElseStatements);
            Assert.Single(chooseBranchThen);
            Assert.Single(chooseBranchElse);
            Assert.Equal("assignment", chooseBranchThen[0].Kind);
            Assert.Equal("result", chooseBranchThen[0].Name);
            Assert.Equal("name", chooseBranchThen[0].Expression.Kind);
            Assert.Equal("left", chooseBranchThen[0].Expression.Name);
            Assert.Equal("assignment", chooseBranchElse[0].Kind);
            Assert.Equal("result", chooseBranchElse[0].Name);
            Assert.Equal("name", chooseBranchElse[0].Expression.Kind);
            Assert.Equal("right", chooseBranchElse[0].Expression.Name);

            var chooseBranchReturn = chooseBranch.TypedBody.Statements[2];
            Assert.Equal("return", chooseBranchReturn.Kind);
            Assert.Equal("name", chooseBranchReturn.Expression.Kind);
            Assert.Equal("result", chooseBranchReturn.Expression.Name);

            var sumTo = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.SumTo");
            Assert.NotNull(sumTo.TypedBody);
            Assert.Equal(4, sumTo.TypedBody!.Statements.Count);

            var sumToIndex = sumTo.TypedBody.Statements[0];
            Assert.Equal("local-variable", sumToIndex.Kind);
            Assert.Equal("index", sumToIndex.Name);
            Assert.True(sumToIndex.IsMutable);
            Assert.Equal("literal", sumToIndex.Expression.Kind);

            var sumToSum = sumTo.TypedBody.Statements[1];
            Assert.Equal("local-variable", sumToSum.Kind);
            Assert.Equal("sum", sumToSum.Name);
            Assert.True(sumToSum.IsMutable);
            Assert.Equal("literal", sumToSum.Expression.Kind);

            var sumToWhile = sumTo.TypedBody.Statements[2];
            Assert.Equal("while", sumToWhile.Kind);
            Assert.Equal("binary", sumToWhile.Expression.Kind);
            Assert.Equal("<", sumToWhile.Expression.Name);
            Assert.Equal(2, sumToWhile.Expression.Arguments!.Count);
            Assert.Equal("name", sumToWhile.Expression.Arguments[0].Kind);
            Assert.Equal("index", sumToWhile.Expression.Arguments[0].Name);
            Assert.Equal("name", sumToWhile.Expression.Arguments[1].Kind);
            Assert.Equal("count", sumToWhile.Expression.Arguments[1].Name);
            var sumToBody = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(sumToWhile.BodyStatements);
            Assert.Equal(2, sumToBody.Count);
            Assert.Equal("assignment", sumToBody[0].Kind);
            Assert.Equal("sum", sumToBody[0].Name);
            Assert.Equal("binary", sumToBody[0].Expression.Kind);
            Assert.Equal("+", sumToBody[0].Expression.Name);
            Assert.Equal("assignment", sumToBody[1].Kind);
            Assert.Equal("index", sumToBody[1].Name);
            Assert.Equal("binary", sumToBody[1].Expression.Kind);
            Assert.Equal("+", sumToBody[1].Expression.Name);

            var sumToReturn = sumTo.TypedBody.Statements[3];
            Assert.Equal("return", sumToReturn.Kind);
            Assert.Equal("name", sumToReturn.Expression.Kind);
            Assert.Equal("sum", sumToReturn.Expression.Name);

            var sumFor = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.SumFor");
            Assert.NotNull(sumFor.TypedBody);
            Assert.Equal(3, sumFor.TypedBody!.Statements.Count);

            var sumForSum = sumFor.TypedBody.Statements[0];
            Assert.Equal("local-variable", sumForSum.Kind);
            Assert.Equal("sum", sumForSum.Name);
            Assert.True(sumForSum.IsMutable);
            Assert.Equal("literal", sumForSum.Expression.Kind);

            var sumForLoop = sumFor.TypedBody.Statements[1];
            Assert.Equal("for", sumForLoop.Kind);
            Assert.Equal("binary", sumForLoop.Expression.Kind);
            Assert.Equal("<", sumForLoop.Expression.Name);
            Assert.Equal(2, sumForLoop.Expression.Arguments!.Count);
            var sumForInitializer = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(sumForLoop.InitializerStatements);
            Assert.Single(sumForInitializer);
            Assert.Equal("local-variable", sumForInitializer[0].Kind);
            Assert.Equal("index", sumForInitializer[0].Name);
            Assert.True(sumForInitializer[0].IsMutable);
            Assert.Equal("literal", sumForInitializer[0].Expression.Kind);
            var sumForIterator = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(sumForLoop.IteratorStatements);
            Assert.Single(sumForIterator);
            Assert.Equal("assignment", sumForIterator[0].Kind);
            Assert.Equal("index", sumForIterator[0].Name);
            Assert.Equal("binary", sumForIterator[0].Expression.Kind);
            Assert.Equal("+", sumForIterator[0].Expression.Name);
            var sumForBody = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(sumForLoop.BodyStatements);
            Assert.Single(sumForBody);
            Assert.Equal("assignment", sumForBody[0].Kind);
            Assert.Equal("sum", sumForBody[0].Name);
            Assert.Equal("binary", sumForBody[0].Expression.Kind);
            Assert.Equal("+", sumForBody[0].Expression.Name);

            var sumForReturn = sumFor.TypedBody.Statements[2];
            Assert.Equal("return", sumForReturn.Kind);
            Assert.Equal("name", sumForReturn.Expression.Kind);
            Assert.Equal("sum", sumForReturn.Expression.Name);

            var sumForControl = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.SumForControl");
            Assert.NotNull(sumForControl.TypedBody);
            Assert.Equal(3, sumForControl.TypedBody!.Statements.Count);

            var sumForControlLoop = sumForControl.TypedBody.Statements[1];
            Assert.Equal("for", sumForControlLoop.Kind);
            var sumForControlBody = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(sumForControlLoop.BodyStatements);
            Assert.Equal(3, sumForControlBody.Count);

            Assert.Equal("if", sumForControlBody[0].Kind);
            var continueBranch = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(sumForControlBody[0].ThenStatements);
            Assert.Single(continueBranch);
            Assert.Equal("continue", continueBranch[0].Kind);
            Assert.Null(continueBranch[0].Expression);

            Assert.Equal("if", sumForControlBody[1].Kind);
            var breakBranch = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(sumForControlBody[1].ThenStatements);
            Assert.Single(breakBranch);
            Assert.Equal("break", breakBranch[0].Kind);
            Assert.Null(breakBranch[0].Expression);

            Assert.Equal("assignment", sumForControlBody[2].Kind);
            Assert.Equal("sum", sumForControlBody[2].Name);

            var one = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.One");
            Assert.NotNull(one.TypedBody);
            var oneReturn = Assert.Single(one.TypedBody!.Statements);
            Assert.Equal("return", oneReturn.Kind);
            Assert.Equal("literal", oneReturn.Expression.Kind);
            Assert.Equal("1", oneReturn.Expression.LiteralText);
            Assert.NotNull(oneReturn.Expression.Type);
            Assert.Equal("integer", oneReturn.Expression.Type!.Kind);
            Assert.Equal(8, oneReturn.Expression.Type.BitWidth);

            var negateFlag = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.NegateFlag");
            Assert.NotNull(negateFlag.TypedBody);
            var negateFlagReturn = Assert.Single(negateFlag.TypedBody!.Statements);
            Assert.Equal("return", negateFlagReturn.Kind);
            Assert.Equal("unary", negateFlagReturn.Expression.Kind);
            Assert.Equal("!", negateFlagReturn.Expression.Name);
            var negateFlagArgument = Assert.Single(negateFlagReturn.Expression.Arguments!);
            Assert.Equal("name", negateFlagArgument.Kind);
            Assert.Equal("flag", negateFlagArgument.Name);

            var addTagged = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.AddTagged");
            Assert.NotNull(addTagged.TypedBody);
            var addTaggedReturn = Assert.Single(addTagged.TypedBody!.Statements);
            Assert.Equal("return", addTaggedReturn.Kind);
            Assert.Equal("binary", addTaggedReturn.Expression.Kind);
            Assert.Equal("+", addTaggedReturn.Expression.Name);
            Assert.Equal(2, addTaggedReturn.Expression.Arguments!.Count);
            Assert.Equal("name", addTaggedReturn.Expression.Arguments[0].Kind);
            Assert.Equal("left", addTaggedReturn.Expression.Arguments[0].Name);
            Assert.Equal("name", addTaggedReturn.Expression.Arguments[1].Kind);
            Assert.Equal("right", addTaggedReturn.Expression.Arguments[1].Name);

            var both = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.Both");
            Assert.NotNull(both.TypedBody);
            var bothReturn = Assert.Single(both.TypedBody!.Statements);
            Assert.Equal("return", bothReturn.Kind);
            Assert.Equal("binary", bothReturn.Expression.Kind);
            Assert.Equal("&&", bothReturn.Expression.Name);
            Assert.Equal(2, bothReturn.Expression.Arguments!.Count);
            Assert.Equal("name", bothReturn.Expression.Arguments[0].Kind);
            Assert.Equal("left", bothReturn.Expression.Arguments[0].Name);
            Assert.Equal("name", bothReturn.Expression.Arguments[1].Kind);
            Assert.Equal("right", bothReturn.Expression.Arguments[1].Name);

            var readValue = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadValue");
            Assert.NotNull(readValue.TypedBody);
            var readValueReturn = Assert.Single(readValue.TypedBody!.Statements);
            Assert.Equal("return", readValueReturn.Kind);
            Assert.Equal("field-access", readValueReturn.Expression.Kind);
            Assert.Equal(0, readValueReturn.Expression.Ordinal);
            var readValueReceiver = Assert.Single(readValueReturn.Expression.Arguments!);
            Assert.Equal("name", readValueReceiver.Kind);
            Assert.Equal("box", readValueReceiver.Name);

            var callEcho = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.CallEcho");
            Assert.NotNull(callEcho.TypedBody);
            var callEchoReturn = Assert.Single(callEcho.TypedBody!.Statements);
            Assert.Equal("return", callEchoReturn.Kind);
            Assert.Equal("member-call", callEchoReturn.Expression.Kind);
            Assert.Equal(0, callEchoReturn.Expression.Ordinal);
            Assert.Equal(2, callEchoReturn.Expression.Arguments!.Count);
            Assert.Equal("name", callEchoReturn.Expression.Arguments[0].Kind);
            Assert.Equal("box", callEchoReturn.Expression.Arguments[0].Name);
            Assert.Equal("name", callEchoReturn.Expression.Arguments[1].Kind);
            Assert.Equal("value", callEchoReturn.Expression.Arguments[1].Name);

            var callHeldEcho = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.CallHeldEcho");
            Assert.NotNull(callHeldEcho.TypedBody);
            var callHeldEchoReturn = Assert.Single(callHeldEcho.TypedBody!.Statements);
            Assert.Equal("return", callHeldEchoReturn.Kind);
            Assert.Equal("member-call", callHeldEchoReturn.Expression.Kind);
            Assert.NotNull(callHeldEchoReturn.Expression.Ordinal);
            Assert.Equal(2, callHeldEchoReturn.Expression.Arguments!.Count);
            Assert.Equal("field-access", callHeldEchoReturn.Expression.Arguments[0].Kind);
            var callHeldEchoReceiver = Assert.Single(callHeldEchoReturn.Expression.Arguments[0].Arguments!);
            Assert.Equal("name", callHeldEchoReceiver.Kind);
            Assert.Equal("holder", callHeldEchoReceiver.Name);
            Assert.Equal("name", callHeldEchoReturn.Expression.Arguments[1].Kind);
            Assert.Equal("value", callHeldEchoReturn.Expression.Arguments[1].Name);

            var callIndexedEcho = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.CallIndexedEcho");
            Assert.NotNull(callIndexedEcho.TypedBody);
            var callIndexedEchoReturn = Assert.Single(callIndexedEcho.TypedBody!.Statements);
            Assert.Equal("return", callIndexedEchoReturn.Kind);
            Assert.Equal("member-call", callIndexedEchoReturn.Expression.Kind);
            Assert.NotNull(callIndexedEchoReturn.Expression.Ordinal);
            Assert.Equal(2, callIndexedEchoReturn.Expression.Arguments!.Count);
            Assert.Equal("index-access", callIndexedEchoReturn.Expression.Arguments[0].Kind);
            var callIndexedEchoReceiverArguments = callIndexedEchoReturn.Expression.Arguments[0].Arguments;
            Assert.NotNull(callIndexedEchoReceiverArguments);
            Assert.Equal(2, callIndexedEchoReceiverArguments.Count);
            Assert.Equal("name", callIndexedEchoReceiverArguments[0].Kind);
            Assert.Equal("boxes", callIndexedEchoReceiverArguments[0].Name);
            Assert.Equal("name", callIndexedEchoReceiverArguments[1].Kind);
            Assert.Equal("index", callIndexedEchoReceiverArguments[1].Name);
            Assert.Equal("name", callIndexedEchoReturn.Expression.Arguments[1].Kind);
            Assert.Equal("value", callIndexedEchoReturn.Expression.Arguments[1].Name);

            var callMadeEcho = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.CallMadeEcho");
            Assert.NotNull(callMadeEcho.TypedBody);
            var callMadeEchoReturn = Assert.Single(callMadeEcho.TypedBody!.Statements);
            Assert.Equal("return", callMadeEchoReturn.Kind);
            Assert.Equal("member-call", callMadeEchoReturn.Expression.Kind);
            Assert.NotNull(callMadeEchoReturn.Expression.Ordinal);
            Assert.Equal(2, callMadeEchoReturn.Expression.Arguments!.Count);
            Assert.Equal("direct-call", callMadeEchoReturn.Expression.Arguments[0].Kind);
            Assert.NotNull(callMadeEchoReturn.Expression.Arguments[0].Ordinal);
            Assert.Equal("name", callMadeEchoReturn.Expression.Arguments[1].Kind);
            Assert.Equal("value", callMadeEchoReturn.Expression.Arguments[1].Name);

            var readMadeValue = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadMadeValue");
            Assert.NotNull(readMadeValue.TypedBody);
            var readMadeValueReturn = Assert.Single(readMadeValue.TypedBody!.Statements);
            Assert.Equal("return", readMadeValueReturn.Kind);
            Assert.Equal("field-access", readMadeValueReturn.Expression.Kind);
            Assert.NotNull(readMadeValueReturn.Expression.Ordinal);
            var readMadeValueReceiver = Assert.Single(readMadeValueReturn.Expression.Arguments!);
            Assert.Equal("direct-call", readMadeValueReceiver.Kind);
            Assert.NotNull(readMadeValueReceiver.Ordinal);

            var callConstructedEcho = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.CallConstructedEcho");
            Assert.NotNull(callConstructedEcho.TypedBody);
            var callConstructedEchoReturn = Assert.Single(callConstructedEcho.TypedBody!.Statements);
            Assert.Equal("return", callConstructedEchoReturn.Kind);
            Assert.Equal("member-call", callConstructedEchoReturn.Expression.Kind);
            Assert.NotNull(callConstructedEchoReturn.Expression.Ordinal);
            Assert.Equal(2, callConstructedEchoReturn.Expression.Arguments!.Count);
            Assert.Equal("object-creation", callConstructedEchoReturn.Expression.Arguments[0].Kind);
            Assert.NotNull(callConstructedEchoReturn.Expression.Arguments[0].Ordinal);
            Assert.Equal("name", callConstructedEchoReturn.Expression.Arguments[1].Kind);
            Assert.Equal("value", callConstructedEchoReturn.Expression.Arguments[1].Name);

            var readConstructedValue = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadConstructedValue");
            Assert.NotNull(readConstructedValue.TypedBody);
            var readConstructedValueReturn = Assert.Single(readConstructedValue.TypedBody!.Statements);
            Assert.Equal("return", readConstructedValueReturn.Kind);
            Assert.Equal("field-access", readConstructedValueReturn.Expression.Kind);
            Assert.NotNull(readConstructedValueReturn.Expression.Ordinal);
            var readConstructedValueReceiver = Assert.Single(readConstructedValueReturn.Expression.Arguments!);
            Assert.Equal("object-creation", readConstructedValueReceiver.Kind);
            Assert.NotNull(readConstructedValueReceiver.Ordinal);

            var chooseBoxValue = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ChooseBoxValue");
            Assert.NotNull(chooseBoxValue.TypedBody);
            var chooseBoxValueReturn = Assert.Single(chooseBoxValue.TypedBody!.Statements);
            Assert.Equal("return", chooseBoxValueReturn.Kind);
            Assert.Equal("field-access", chooseBoxValueReturn.Expression.Kind);
            Assert.NotNull(chooseBoxValueReturn.Expression.Ordinal);
            var chooseBoxValueReceiver = Assert.Single(chooseBoxValueReturn.Expression.Arguments!);
            Assert.Equal("conditional", chooseBoxValueReceiver.Kind);
            Assert.Equal(3, chooseBoxValueReceiver.Arguments!.Count);

            var chooseEcho = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ChooseEcho");
            Assert.NotNull(chooseEcho.TypedBody);
            var chooseEchoReturn = Assert.Single(chooseEcho.TypedBody!.Statements);
            Assert.Equal("return", chooseEchoReturn.Kind);
            Assert.Equal("member-call", chooseEchoReturn.Expression.Kind);
            Assert.NotNull(chooseEchoReturn.Expression.Ordinal);
            Assert.Equal(2, chooseEchoReturn.Expression.Arguments!.Count);
            Assert.Equal("conditional", chooseEchoReturn.Expression.Arguments[0].Kind);
            Assert.Equal(3, chooseEchoReturn.Expression.Arguments[0].Arguments!.Count);
            Assert.Equal("name", chooseEchoReturn.Expression.Arguments[1].Kind);
            Assert.Equal("value", chooseEchoReturn.Expression.Arguments[1].Name);

            var readSliceAt = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadSliceAt");
            Assert.NotNull(readSliceAt.TypedBody);
            var readSliceAtReturn = Assert.Single(readSliceAt.TypedBody!.Statements);
            Assert.Equal("return", readSliceAtReturn.Kind);
            Assert.Equal("index-access", readSliceAtReturn.Expression.Kind);
            Assert.Equal(2, readSliceAtReturn.Expression.Arguments!.Count);
            Assert.Equal("name", readSliceAtReturn.Expression.Arguments[0].Kind);
            Assert.Equal("view", readSliceAtReturn.Expression.Arguments[0].Name);
            Assert.Equal("name", readSliceAtReturn.Expression.Arguments[1].Kind);
            Assert.Equal("index", readSliceAtReturn.Expression.Arguments[1].Name);

            var sliceAsciiWindow = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.SliceAsciiWindow");
            Assert.NotNull(sliceAsciiWindow.TypedBody);
            var sliceAsciiWindowReturn = Assert.Single(sliceAsciiWindow.TypedBody!.Statements);
            Assert.Equal("return", sliceAsciiWindowReturn.Kind);
            Assert.Equal("index-access", sliceAsciiWindowReturn.Expression.Kind);
            Assert.Equal(3, sliceAsciiWindowReturn.Expression.Arguments!.Count);
            Assert.Equal("name", sliceAsciiWindowReturn.Expression.Arguments[0].Kind);
            Assert.Equal("text", sliceAsciiWindowReturn.Expression.Arguments[0].Name);
            Assert.Equal("name", sliceAsciiWindowReturn.Expression.Arguments[1].Kind);
            Assert.Equal("start", sliceAsciiWindowReturn.Expression.Arguments[1].Name);
            Assert.Equal("name", sliceAsciiWindowReturn.Expression.Arguments[2].Kind);
            Assert.Equal("length", sliceAsciiWindowReturn.Expression.Arguments[2].Name);

            var readBoxSliceAt = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadBoxSliceAt");
            Assert.NotNull(readBoxSliceAt.TypedBody);
            var readBoxSliceAtReturn = Assert.Single(readBoxSliceAt.TypedBody!.Statements);
            Assert.Equal("return", readBoxSliceAtReturn.Kind);
            Assert.Equal("index-access", readBoxSliceAtReturn.Expression.Kind);
            Assert.Equal(2, readBoxSliceAtReturn.Expression.Arguments!.Count);
            Assert.Equal("field-access", readBoxSliceAtReturn.Expression.Arguments[0].Kind);
            Assert.NotNull(readBoxSliceAtReturn.Expression.Arguments[0].Ordinal);
            var readBoxSliceReceiver = Assert.Single(readBoxSliceAtReturn.Expression.Arguments[0].Arguments!);
            Assert.Equal("name", readBoxSliceReceiver.Kind);
            Assert.Equal("box", readBoxSliceReceiver.Name);
            Assert.Equal("name", readBoxSliceAtReturn.Expression.Arguments[1].Kind);
            Assert.Equal("index", readBoxSliceAtReturn.Expression.Arguments[1].Name);

            var readIndexedCount = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadIndexedCount");
            Assert.NotNull(readIndexedCount.TypedBody);
            var readIndexedCountReturn = Assert.Single(readIndexedCount.TypedBody!.Statements);
            Assert.Equal("return", readIndexedCountReturn.Kind);
            Assert.Equal("field-access", readIndexedCountReturn.Expression.Kind);
            Assert.NotNull(readIndexedCountReturn.Expression.Ordinal);
            var readIndexedCountReceiver = Assert.Single(readIndexedCountReturn.Expression.Arguments!);
            Assert.Equal("index-access", readIndexedCountReceiver.Kind);
            Assert.Equal(2, readIndexedCountReceiver.Arguments!.Count);
            Assert.Equal("name", readIndexedCountReceiver.Arguments[0].Kind);
            Assert.Equal("pairs", readIndexedCountReceiver.Arguments[0].Name);
            Assert.Equal("name", readIndexedCountReceiver.Arguments[1].Kind);
            Assert.Equal("index", readIndexedCountReceiver.Arguments[1].Name);

            var forwardReset = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ForwardReset");
            Assert.NotNull(forwardReset.TypedBody);
            var forwardResetExpression = Assert.Single(forwardReset.TypedBody!.Statements);
            Assert.Equal("expression", forwardResetExpression.Kind);
            Assert.Equal("direct-call", forwardResetExpression.Expression.Kind);
            Assert.Equal(0, forwardResetExpression.Expression.Ordinal);
            var forwardResetArgument = Assert.Single(forwardResetExpression.Expression.Arguments!);
            Assert.Equal("name", forwardResetArgument.Kind);
            Assert.Equal("box", forwardResetArgument.Name);

            var forwardMethodReset = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ForwardMethodReset");
            Assert.NotNull(forwardMethodReset.TypedBody);
            var forwardMethodResetExpression = Assert.Single(forwardMethodReset.TypedBody!.Statements);
            Assert.Equal("expression", forwardMethodResetExpression.Kind);
            Assert.Equal("member-call", forwardMethodResetExpression.Expression.Kind);
            Assert.Equal(0, forwardMethodResetExpression.Expression.Ordinal);
            var forwardMethodResetReceiver = Assert.Single(forwardMethodResetExpression.Expression.Arguments!);
            Assert.Equal("name", forwardMethodResetReceiver.Kind);
            Assert.Equal("box", forwardMethodResetReceiver.Name);

            var guardedReset = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.GuardedReset");
            Assert.NotNull(guardedReset.TypedBody);
            Assert.Equal(3, guardedReset.TypedBody!.Statements.Count);

            var guardedResetIf = guardedReset.TypedBody.Statements[0];
            Assert.Equal("if", guardedResetIf.Kind);
            Assert.Equal("name", guardedResetIf.Expression.Kind);
            Assert.Equal("shouldStop", guardedResetIf.Expression.Name);
            var guardedResetThen = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateStatementManifest>>(guardedResetIf.ThenStatements);
            var guardedResetThenReturn = Assert.Single(guardedResetThen);
            Assert.Equal("return", guardedResetThenReturn.Kind);
            Assert.Null(guardedResetThenReturn.Expression);

            var guardedResetExpression = guardedReset.TypedBody.Statements[1];
            Assert.Equal("expression", guardedResetExpression.Kind);
            Assert.Equal("direct-call", guardedResetExpression.Expression.Kind);
            Assert.Equal(0, guardedResetExpression.Expression.Ordinal);
            var guardedResetArgument = Assert.Single(guardedResetExpression.Expression.Arguments!);
            Assert.Equal("name", guardedResetArgument.Kind);
            Assert.Equal("box", guardedResetArgument.Name);

            var guardedResetReturn = guardedReset.TypedBody.Statements[2];
            Assert.Equal("return", guardedResetReturn.Kind);
            Assert.Null(guardedResetReturn.Expression);

            var wrap = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.Wrap");
            Assert.NotNull(wrap.TypedBody);
            var wrapReturn = Assert.Single(wrap.TypedBody!.Statements);
            Assert.Equal("return", wrapReturn.Kind);
            Assert.Equal("object-creation", wrapReturn.Expression.Kind);
            Assert.Equal(0, wrapReturn.Expression.Ordinal);
            var wrapArgument = Assert.Single(wrapReturn.Expression.Arguments!);
            Assert.Equal("name", wrapArgument.Kind);
            Assert.Equal("value", wrapArgument.Name);

            var wrapOption = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.WrapOption");
            Assert.NotNull(wrapOption.TypedBody);
            var wrapOptionReturn = Assert.Single(wrapOption.TypedBody!.Statements);
            Assert.Equal("return", wrapOptionReturn.Kind);
            Assert.Equal("enum-call", wrapOptionReturn.Expression.Kind);
            Assert.Equal(0, wrapOptionReturn.Expression.Ordinal);
            var wrapOptionArgument = Assert.Single(wrapOptionReturn.Expression.Arguments!);
            Assert.Equal("name", wrapOptionArgument.Kind);
            Assert.Equal("value", wrapOptionArgument.Name);

            var emptyLike = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.EmptyLike");
            Assert.NotNull(emptyLike.TypedBody);
            var emptyLikeReturn = Assert.Single(emptyLike.TypedBody!.Statements);
            Assert.Equal("return", emptyLikeReturn.Kind);
            Assert.Equal("enum-value", emptyLikeReturn.Expression.Kind);
            Assert.Equal(0, emptyLikeReturn.Expression.Ordinal);
            Assert.True(emptyLikeReturn.Expression.Arguments is null || emptyLikeReturn.Expression.Arguments.Count == 0);

            var wrapNamed = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.WrapNamed");
            Assert.NotNull(wrapNamed.TypedBody);
            var wrapNamedReturn = Assert.Single(wrapNamed.TypedBody!.Statements);
            Assert.Equal("return", wrapNamedReturn.Kind);
            Assert.Equal("enum-constructor", wrapNamedReturn.Expression.Kind);
            Assert.Equal(0, wrapNamedReturn.Expression.Ordinal);
            Assert.Equal(2, wrapNamedReturn.Expression.Arguments!.Count);
            Assert.Equal("name", wrapNamedReturn.Expression.Arguments[0].Kind);
            Assert.Equal("value", wrapNamedReturn.Expression.Arguments[0].Name);
            Assert.Equal("name", wrapNamedReturn.Expression.Arguments[1].Kind);
            Assert.Equal("tag", wrapNamedReturn.Expression.Arguments[1].Name);

            var wrapNamedConst = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.WrapNamedConst");
            Assert.NotNull(wrapNamedConst.TypedBody);
            var wrapNamedConstReturn = Assert.Single(wrapNamedConst.TypedBody!.Statements);
            Assert.Equal("return", wrapNamedConstReturn.Kind);
            Assert.Equal("enum-constructor", wrapNamedConstReturn.Expression.Kind);
            Assert.Equal(0, wrapNamedConstReturn.Expression.Ordinal);
            Assert.Equal(2, wrapNamedConstReturn.Expression.Arguments!.Count);
            Assert.Equal("name", wrapNamedConstReturn.Expression.Arguments[0].Kind);
            Assert.Equal("value", wrapNamedConstReturn.Expression.Arguments[0].Name);
            Assert.Equal("literal", wrapNamedConstReturn.Expression.Arguments[1].Kind);
            Assert.Equal("1", wrapNamedConstReturn.Expression.Arguments[1].LiteralText);
            var wrapNamedConstLiteralType = Assert.IsType<StarkPackageTypeReference>(wrapNamedConstReturn.Expression.Arguments[1].Type);
            Assert.Equal("integer", wrapNamedConstLiteralType.Kind);
            Assert.Equal(8, wrapNamedConstLiteralType.BitWidth);

            var choose = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.Choose");
            Assert.NotNull(choose.TypedBody);
            var chooseReturn = Assert.Single(choose.TypedBody!.Statements);
            Assert.Equal("return", chooseReturn.Kind);
            Assert.Equal("conditional", chooseReturn.Expression.Kind);
            Assert.Equal(3, chooseReturn.Expression.Arguments!.Count);
            Assert.Equal("name", chooseReturn.Expression.Arguments[0].Kind);
            Assert.Equal("takeLeft", chooseReturn.Expression.Arguments[0].Name);
            Assert.Equal("name", chooseReturn.Expression.Arguments[1].Kind);
            Assert.Equal("left", chooseReturn.Expression.Arguments[1].Name);
            Assert.Equal("name", chooseReturn.Expression.Arguments[2].Kind);
            Assert.Equal("right", chooseReturn.Expression.Arguments[2].Name);

            var minTagged = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.MinTagged");
            Assert.NotNull(minTagged.TypedBody);
            var minTaggedReturn = Assert.Single(minTagged.TypedBody!.Statements);
            Assert.Equal("return", minTaggedReturn.Kind);
            Assert.Equal("conditional", minTaggedReturn.Expression.Kind);
            Assert.Equal(3, minTaggedReturn.Expression.Arguments!.Count);
            Assert.Equal("binary", minTaggedReturn.Expression.Arguments[0].Kind);
            Assert.Equal("<", minTaggedReturn.Expression.Arguments[0].Name);
            var minTaggedConditionArguments = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateExpressionManifest>>(minTaggedReturn.Expression.Arguments[0].Arguments);
            Assert.Equal("name", minTaggedConditionArguments[0].Kind);
            Assert.Equal("left", minTaggedConditionArguments[0].Name);
            Assert.Equal("name", minTaggedConditionArguments[1].Kind);
            Assert.Equal("right", minTaggedConditionArguments[1].Name);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedSwitchTemplateBodySubset()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-typed-switch-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Option<T> {
                    None,
                    Some(T),
                }

                public enum Boxed<T> {
                    Value { Data: T, Tag: i32[min max] },
                }

                public enum Wrapped<T> {
                    Value { Data: Counter, Marker: i32[min max] },
                }

                public record Counter(i32[min max] Value, i32[min max] Count) { }

                public fn i32[min max] HasValueSwitch<T>(Option<T> value) {
                    switch (value) {
                        case Option<T>.Some(var payload):
                            return 1;
                        case Option<T>.None:
                            return 0;
                    }
                }

                public fn i32[min max] ReadTagSwitch<T>(Boxed<T> boxed) {
                    switch (boxed) {
                        case Boxed<T>.Value { Data: _, Tag: var tag }:
                            return tag;
                    }
                }

                public fn i32[min max] ReadCountSwitch<T>(Counter counter, T tag) {
                    switch (counter) {
                        case Counter(_, var count):
                            return count;
                    }
                }

                public fn i32[min max] ClassifySwitch<T>(i32[min max] value, T tag) {
                    switch (value) {
                        case 0:
                        case 1:
                            return 10;
                        case var current when current > 5:
                            return current;
                        default:
                            return -1;
                    }
                }

                public fn i32[min max] ReadNestedCountSwitch<T>(Wrapped<T> wrapped, T tag) {
                    switch (wrapped) {
                        case Wrapped<T>.Value { Data: Counter(7, var count), Marker: 1 }:
                            return count;
                        default:
                            return -1;
                    }
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var hasValueSwitch = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.HasValueSwitch");
            Assert.NotNull(hasValueSwitch.TypedBody);
            var hasValueSwitchStatement = Assert.Single(hasValueSwitch.TypedBody!.Statements);
            Assert.Equal("switch", hasValueSwitchStatement.Kind);
            Assert.Equal("name", hasValueSwitchStatement.Expression.Kind);
            Assert.Equal("value", hasValueSwitchStatement.Expression.Name);
            var hasValueSwitchCases = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateSwitchCaseManifest>>(hasValueSwitchStatement.SwitchCases);
            Assert.Equal(2, hasValueSwitchCases.Count);
            Assert.Equal("enum-pattern", hasValueSwitchCases[0].Kind);
            Assert.Single(hasValueSwitchCases[0].Members!);
            Assert.Equal("capture", hasValueSwitchCases[0].Members![0].Kind);
            Assert.Equal("payload", hasValueSwitchCases[0].Members![0].Name);
            Assert.Equal("enum-pattern", hasValueSwitchCases[1].Kind);
            Assert.Empty(hasValueSwitchCases[1].Members ?? []);

            var readTagSwitch = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadTagSwitch");
            Assert.NotNull(readTagSwitch.TypedBody);
            var readTagSwitchStatement = Assert.Single(readTagSwitch.TypedBody!.Statements);
            Assert.Equal("switch", readTagSwitchStatement.Kind);
            var readTagSwitchCase = Assert.Single(readTagSwitchStatement.SwitchCases!);
            Assert.Equal("enum-pattern", readTagSwitchCase.Kind);
            Assert.Equal(2, readTagSwitchCase.Members!.Count);
            Assert.Equal("discard", readTagSwitchCase.Members[0].Kind);
            Assert.Equal("capture", readTagSwitchCase.Members[1].Kind);
            Assert.Equal("tag", readTagSwitchCase.Members[1].Name);

            var readCountSwitch = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadCountSwitch");
            Assert.NotNull(readCountSwitch.TypedBody);
            var readCountSwitchStatement = Assert.Single(readCountSwitch.TypedBody!.Statements);
            Assert.Equal("switch", readCountSwitchStatement.Kind);
            var readCountSwitchCase = Assert.Single(readCountSwitchStatement.SwitchCases!);
            Assert.Equal("aggregate-pattern", readCountSwitchCase.Kind);
            Assert.Equal(2, readCountSwitchCase.Members!.Count);
            Assert.Equal("discard", readCountSwitchCase.Members[0].Kind);
            Assert.Equal("capture", readCountSwitchCase.Members[1].Kind);
            Assert.Equal("count", readCountSwitchCase.Members[1].Name);

            var classifySwitch = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ClassifySwitch");
            Assert.NotNull(classifySwitch.TypedBody);
            var classifySwitchStatement = Assert.Single(classifySwitch.TypedBody!.Statements);
            Assert.Equal("switch", classifySwitchStatement.Kind);
            Assert.Equal("name", classifySwitchStatement.Expression.Kind);
            Assert.Equal("value", classifySwitchStatement.Expression.Name);
            var classifySwitchCases = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateSwitchCaseManifest>>(classifySwitchStatement.SwitchCases);
            Assert.Equal(4, classifySwitchCases.Count);

            Assert.Equal("literal", classifySwitchCases[0].Kind);
            Assert.Equal("0", classifySwitchCases[0].Expression!.LiteralText);
            Assert.Single(classifySwitchCases[0].Statements!);

            Assert.Equal("literal", classifySwitchCases[1].Kind);
            Assert.Equal("1", classifySwitchCases[1].Expression!.LiteralText);
            Assert.Single(classifySwitchCases[1].Statements!);

            Assert.Equal("match-all", classifySwitchCases[2].Kind);
            Assert.Equal("current", classifySwitchCases[2].Name);
            var classifySwitchGuard = Assert.IsType<StarkPackageTypedTemplateExpressionManifest>(classifySwitchCases[2].GuardExpression);
            Assert.Equal("binary", classifySwitchGuard.Kind);
            Assert.Equal(">", classifySwitchGuard.Name);
            var classifySwitchGuardArguments = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateExpressionManifest>>(classifySwitchGuard.Arguments);
            Assert.Equal(2, classifySwitchGuardArguments.Count);
            Assert.Equal("name", classifySwitchGuardArguments[0].Kind);
            Assert.Equal("current", classifySwitchGuardArguments[0].Name);
            Assert.Equal("literal", classifySwitchGuardArguments[1].Kind);

            Assert.Equal("default", classifySwitchCases[3].Kind);
            Assert.Single(classifySwitchCases[3].Statements!);

            var readNestedCountSwitch = Assert.Single(facadeModule.GenericTemplates.Functions, static item => item.QualifiedResolvedName == "Facade.ReadNestedCountSwitch");
            Assert.NotNull(readNestedCountSwitch.TypedBody);
            var readNestedCountSwitchStatement = Assert.Single(readNestedCountSwitch.TypedBody!.Statements);
            Assert.Equal("switch", readNestedCountSwitchStatement.Kind);
            var readNestedCountCases = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplateSwitchCaseManifest>>(readNestedCountSwitchStatement.SwitchCases);
            Assert.Equal(2, readNestedCountCases.Count);
            Assert.Equal("enum-pattern", readNestedCountCases[0].Kind);
            var readNestedCountMembers = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplatePatternManifest>>(readNestedCountCases[0].Members);
            Assert.Equal(2, readNestedCountMembers.Count);
            Assert.Equal("aggregate-pattern", readNestedCountMembers[0].Kind);
            var nestedAggregateMembers = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTypedTemplatePatternManifest>>(readNestedCountMembers[0].Members);
            Assert.Equal(2, nestedAggregateMembers.Count);
            Assert.Equal("literal", nestedAggregateMembers[0].Kind);
            Assert.Equal("7", nestedAggregateMembers[0].Expression!.LiteralText);
            Assert.Equal("capture", nestedAggregateMembers[1].Kind);
            Assert.Equal("count", nestedAggregateMembers[1].Name);
            Assert.Equal("literal", readNestedCountMembers[1].Kind);
            Assert.Equal("1", readNestedCountMembers[1].Expression!.LiteralText);
            Assert.Equal("default", readNestedCountCases[1].Kind);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateConversionFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-conversions-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Truncate<T>(f32 value, T tag) {
                    return (i32[min max])value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Truncate");

            var conversion = Assert.Single(template.Conversions!);
            Assert.Equal(0, conversion.Ordinal);
            Assert.Equal("integer", conversion.TargetType.Kind);
            Assert.Equal(32, conversion.TargetType.BitWidth);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateEnumConstructorFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-enum-constructors-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T> {
                    Value { Data: T, Tag: i8[min max] },
                }

                public fn Boxed<T> Wrap<T>(T value) {
                    return Boxed<T>.Value { Data: value, Tag: 1 };
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Wrap");

            var enumConstructor = Assert.Single(template.EnumConstructors!);
            Assert.Equal(0, enumConstructor.Ordinal);
            Assert.Equal("named", enumConstructor.EnumType.Kind);
            Assert.Equal("Facade.Boxed", enumConstructor.EnumType.Name);
            Assert.Equal("T", Assert.Single(enumConstructor.EnumType.TypeArguments!).Name);
            Assert.Equal("Value", enumConstructor.VariantName);
            Assert.Equal(2, enumConstructor.Members!.Count);

            var dataMember = enumConstructor.Members[0];
            Assert.Equal("Data", dataMember.FieldName);
            Assert.Equal(0, dataMember.FieldIndex);
            Assert.Equal("named", dataMember.FieldType.Kind);
            Assert.Equal("T", dataMember.FieldType.Name);

            var tagMember = enumConstructor.Members[1];
            Assert.Equal("Tag", tagMember.FieldName);
            Assert.Equal(1, tagMember.FieldIndex);
            Assert.Equal("integer", tagMember.FieldType.Kind);
            Assert.Equal(8, tagMember.FieldType.BitWidth);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateEnumCallFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-enum-calls-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Option<T> {
                    None,
                    Some(T),
                }

                public fn Option<T> Wrap<T>(T value) {
                    return Option<T>.Some(value);
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Wrap");

            var enumCall = Assert.Single(template.EnumCalls!);
            Assert.Equal(0, enumCall.Ordinal);
            Assert.Equal("named", enumCall.EnumType.Kind);
            Assert.Equal("Facade.Option", enumCall.EnumType.Name);
            Assert.Equal("T", Assert.Single(enumCall.EnumType.TypeArguments!).Name);
            Assert.Equal("Some", enumCall.VariantName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateEnumValueFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-enum-values-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Option<T> {
                    None,
                    Some(T),
                }

                public fn Option<T> EmptyLike<T>(T value) {
                    return Option<T>.None;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.EmptyLike");

            var enumValue = Assert.Single(template.EnumValues!);
            Assert.Equal(0, enumValue.Ordinal);
            Assert.Equal("named", enumValue.EnumType.Kind);
            Assert.Equal("Facade.Option", enumValue.EnumType.Name);
            Assert.Equal("T", Assert.Single(enumValue.EnumType.TypeArguments!).Name);
            Assert.Equal("None", enumValue.VariantName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateEnumPatternFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-enum-patterns-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Option<T> {
                    None,
                    Some(T),
                }

                public fn i32[min max] HasValue<T>(Option<T> value) {
                    switch (value) {
                        case Option<T>.Some(var payload):
                            return 1;
                        case Option<T>.None:
                            return 0;
                    }
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.HasValue");
            var enumPatterns = Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTemplateEnumPatternManifest>>(template.EnumPatterns!);
            Assert.Collection(
                enumPatterns,
                static enumPattern =>
                {
                    Assert.Equal(0, enumPattern.Ordinal);
                    Assert.Equal("named", enumPattern.EnumType.Kind);
                    Assert.Equal("Facade.Option", enumPattern.EnumType.Name);
                    Assert.Equal("T", Assert.Single(enumPattern.EnumType.TypeArguments!).Name);
                    Assert.Equal("Some", enumPattern.VariantName);
                },
                static enumPattern =>
                {
                    Assert.Equal(1, enumPattern.Ordinal);
                    Assert.Equal("named", enumPattern.EnumType.Kind);
                    Assert.Equal("Facade.Option", enumPattern.EnumType.Name);
                    Assert.Equal("T", Assert.Single(enumPattern.EnumType.TypeArguments!).Name);
                    Assert.Equal("None", enumPattern.VariantName);
                });

            Assert.Equal(2, enumPatterns.Count);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateEnumPatternMemberFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-enum-pattern-members-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T> {
                    Value { Data: T, Tag: i32[min max] },
                }

                public fn i32[min max] ReadTag<T>(Boxed<T> boxed) {
                    switch (boxed) {
                        case Boxed<T>.Value { Data: _, Tag: var tag }:
                            return tag;
                    }
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.ReadTag");
            var enumPattern = Assert.Single(template.EnumPatterns!);

            Assert.Equal(0, enumPattern.Ordinal);
            Assert.Equal("named", enumPattern.EnumType.Kind);
            Assert.Equal("Facade.Boxed", enumPattern.EnumType.Name);
            Assert.Equal("T", Assert.Single(enumPattern.EnumType.TypeArguments!).Name);
            Assert.Equal("Value", enumPattern.VariantName);
            Assert.Collection(
                Assert.IsAssignableFrom<IReadOnlyList<StarkPackageTemplateEnumPatternMemberManifest>>(enumPattern.Members!),
                static member =>
                {
                    Assert.Equal("Data", member.FieldName);
                    Assert.Equal(0, member.FieldIndex);
                    Assert.Equal("named", member.FieldType.Kind);
                    Assert.Equal("T", member.FieldType.Name);
                },
                static member =>
                {
                    Assert.Equal("Tag", member.FieldName);
                    Assert.Equal(1, member.FieldIndex);
                    Assert.Equal("integer", member.FieldType.Kind);
                    Assert.Equal(32, member.FieldType.BitWidth);
                });
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateAggregatePatternFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-aggregate-patterns-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32[min max] Value, i32[min max] Count) { }

                public fn i32[min max] ReadCount<T>(Counter counter, T tag) {
                    switch (counter) {
                        case Counter(_, var count):
                            return count;
                    }
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.ReadCount");
            var aggregatePattern = Assert.Single(template.AggregatePatterns!);

            Assert.Equal(0, aggregatePattern.Ordinal);
            Assert.Equal("named", aggregatePattern.Type.Kind);
            Assert.Equal("Facade.Counter", aggregatePattern.Type.Name);
            Assert.True(aggregatePattern.Type.TypeArguments is null || aggregatePattern.Type.TypeArguments.Count == 0);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateDirectCallFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-calls-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Forward<T>(T value) {
                    return Identity(value);
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Forward");
            var directCall = Assert.Single(template.DirectCalls!);

            Assert.Equal("Facade.Identity", directCall.QualifiedResolvedName);
            Assert.Equal("Facade.Identity", directCall.QualifiedSourceName);
            Assert.Equal("Facade.Identity", directCall.QualifiedTemplateName);
            Assert.Equal("named", directCall.ReturnType.Kind);
            Assert.Equal("T", directCall.ReturnType.Name);

            var parameter = Assert.Single(directCall.Parameters);
            Assert.Equal("value", parameter.Name);
            Assert.Equal("named", parameter.Type.Kind);
            Assert.Equal("T", parameter.Type.Name);

            var typeArgument = Assert.Single(directCall.TypeArguments!);
            Assert.Equal("named", typeArgument.Kind);
            Assert.Equal("T", typeArgument.Name);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateFieldAccessFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-fields-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T ReadValue<T>(Pair<T> pair) {
                    return pair.Value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.ReadValue");
            var fieldAccess = Assert.Single(template.FieldAccesses!);

            Assert.Equal(0, fieldAccess.Ordinal);
            Assert.Equal("Value", fieldAccess.FieldName);
            Assert.Equal(0, fieldAccess.FieldIndex);
            Assert.Equal("named", fieldAccess.FieldType.Kind);
            Assert.Equal("T", fieldAccess.FieldType.Name);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesTypedGenericTemplateMemberCallFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-member-calls-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32[min max] Dummy) {
                    fn i32[min max] Echo(borrow Box self, i32[min max] value) {
                        return value;
                    }
                }

                public fn i32[min max] Forward<T>(T tag, Box box, i32[min max] value) {
                    return box.Echo(value);
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Forward");
            var memberCall = Assert.Single(template.MemberCalls!);

            Assert.Equal(0, memberCall.Ordinal);
            Assert.Equal("Facade.Box.Echo", memberCall.QualifiedResolvedName);
            Assert.Equal("Facade.Box.Echo", memberCall.QualifiedSourceName);
            Assert.Equal("integer", memberCall.ReturnType.Kind);
            Assert.Equal(32, memberCall.ReturnType.BitWidth);
            Assert.Equal(2, memberCall.Parameters.Count);
            Assert.Equal("self", memberCall.Parameters[0].Name);
            Assert.Equal("Facade.Box", memberCall.Parameters[0].Type.Name);
            Assert.Equal("value", memberCall.Parameters[1].Name);
            Assert.Equal(32, memberCall.Parameters[1].Type.BitWidth);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestPreservesRecordPrimaryConstructorParameters()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-primary-constructor-types-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter<T>(T Value) {
                    i32[min max] Tag;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var counter = Assert.Single(facadeModule.TypedInterface!.Types, static type => type.QualifiedName == "Facade.Counter");

            var primary = Assert.Single(counter.PrimaryConstructorParameters!);
            Assert.Equal("Value", primary.Name);
            Assert.Equal("named", primary.Type.Kind);
            Assert.Equal("T", primary.Type.Name);
            Assert.Contains(counter.Fields, static field => field.Name == "Value");
            Assert.Contains(counter.Fields, static field => field.Name == "Tag");
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesDeferredGenericInstantiationPatterns()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-deferred-templates-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Forward<T>(T value) {
                    return Identity(value);
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.GenericTemplates);

            var templates = facadeModule.GenericTemplates!.Functions;
            var identity = Assert.Single(templates, static template => template.QualifiedResolvedName == "Facade.Identity");
            Assert.True(identity.DeferredFunctionInstantiations is null || identity.DeferredFunctionInstantiations.Count == 0);

            var forward = Assert.Single(templates, static template => template.QualifiedResolvedName == "Facade.Forward");
            var deferred = Assert.Single(forward.DeferredFunctionInstantiations!);
            Assert.Equal("Facade.Identity", deferred.CalleeTemplateName);
            var deferredTypeArgument = Assert.Single(deferred.TypeArguments);
            Assert.Equal("named", deferredTypeArgument.Kind);
            Assert.Equal("T", deferredTypeArgument.Name);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void PackageManifestIncludesDeferredGenericTypeInstantiationPatterns()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-deferred-type-templates-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<A, B>(A First, B Second) { }

                public fn i32[min max] Forward<T>(T value, bool flag) {
                    stack Pair<T, bool> pair = new Pair<T, bool>(value, flag);
                    return pair.Second ? 1 : 0;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.GenericTemplates);

            var templates = facadeModule.GenericTemplates!.Functions;
            var forward = Assert.Single(templates, static template => template.QualifiedResolvedName == "Facade.Forward");
            var deferredType = Assert.Single(forward.DeferredTypeInstantiations!);
            Assert.Equal("named", deferredType.Type.Kind);
            Assert.Equal("Facade.Pair", deferredType.Type.Name);
            Assert.Equal(["T", "bool"], deferredType.Type.TypeArguments!.Select(static type => type.Name ?? type.Kind));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedGenericMethodsPreserveTypeParametersWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-method-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    fn T Echo<T>(T value);
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var boxManifest = Assert.Single(facadeModule.SourceSurface!.Types!, static type => type.Name == "Box");
            Assert.NotNull(boxManifest.Methods);
            var echoManifest = Assert.Single(boxManifest.Methods!, static method => method.Name == "Echo");
            Assert.Equal(["T"], echoManifest.GenericParameters);

            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run() {
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);

            var importedEcho = Assert.Single(importedModule.SyntaxModel.Declarations, static declaration => declaration.Name == "Box.Echo");
            Assert.NotNull(importedEcho.Function);
            Assert.True(importedEcho.Function!.IsGeneric);
            Assert.Equal(["T"], importedEcho.Function.GenericParams);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedTypeAliasesRoundTripWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-alias-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Pair {
                    i32[min max] Left;
                    i32[min max] Right;
                }

                alias HiddenPair = Pair;
                public alias PublicPair = HiddenPair;
                export alias ExportedPair = HiddenPair;
                public alias BufferView<T> = T[];

                public finite law i32[min max] Left(PublicPair value, BufferView<i32[min max]> view) {
                    return value.Left + view[0];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.SourceSurface);

            var typeAliases = facadeModule.SourceSurface!.TypeAliases!;
            Assert.Equal(
                ["BufferView", "ExportedPair", "PublicPair"],
                typeAliases.Select(static alias => alias.Name).OrderBy(static name => name, StringComparer.Ordinal).ToArray());

            var exportedPair = Assert.Single(typeAliases, static alias => alias.Name == "ExportedPair");
            Assert.Equal("Facade.ExportedPair", exportedPair.QualifiedName);
            Assert.Equal("export", exportedPair.Visibility);
            Assert.Equal("HiddenPair", exportedPair.TargetType);

            var publicPair = Assert.Single(typeAliases, static alias => alias.Name == "PublicPair");
            Assert.Equal("Facade.PublicPair", publicPair.QualifiedName);
            Assert.Equal("public", publicPair.Visibility);
            Assert.Equal("HiddenPair", publicPair.TargetType);

            var bufferView = Assert.Single(typeAliases, static alias => alias.Name == "BufferView");
            Assert.Equal("Facade.BufferView", bufferView.QualifiedName);
            Assert.Equal("public", bufferView.Visibility);
            Assert.Equal("T[]", bufferView.TargetType);
            Assert.Equal(["T"], bufferView.GenericParameters);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        stack i32[min max][1] values = { 4 };
                        stack Facade.ExportedPair exported = new Facade.Pair() { Left = 3, Right = 0 };
                        stack Facade.PublicPair pair = exported;
                        stack Facade.BufferView<i32[min max]> view = (i32[min max][])values;
                        return Facade.Left(pair, view);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);

            Assert.Contains(
                importedModule.SyntaxModel.Declarations,
                static declaration => declaration.Kind == DeclarationKind.TypeAlias
                    && declaration.Name == "ExportedPair"
                    && declaration.Visibility == StarkVisibility.Export
                    && declaration.TypeAlias is not null
                    && declaration.TypeAlias.AliasedType == "Pair");
            Assert.Contains(
                importedModule.SyntaxModel.Declarations,
                static declaration => declaration.Kind == DeclarationKind.TypeAlias
                    && declaration.Name == "PublicPair"
                    && declaration.Visibility == StarkVisibility.Public
                    && declaration.TypeAlias is not null
                    && declaration.TypeAlias.AliasedType == "Pair");
            Assert.Contains(
                importedModule.SyntaxModel.Declarations,
                static declaration => declaration.Kind == DeclarationKind.TypeAlias
                    && declaration.Name == "BufferView"
                    && declaration.Visibility == StarkVisibility.Public
                    && declaration.TypeAlias is not null
                    && declaration.TypeAlias.AliasedType == "T[]");
            Assert.DoesNotContain(
                importedModule.SyntaxModel.Declarations,
                static declaration => declaration.Kind == DeclarationKind.TypeAlias
                    && declaration.Name == "HiddenPair");

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.Contains("Facade.ExportedPair", typeCheckModel.TypeAliases.Keys);
            Assert.Contains("Facade.PublicPair", typeCheckModel.TypeAliases.Keys);
            Assert.Contains("Facade.BufferView", typeCheckModel.TypeAliases.Keys);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedTypeAliasesResolveFromPackageImageFactsWhenBridgeAliasSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-alias-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Pair {
                    i32[min max] Left;
                    i32[min max] Right;
                }

                public alias PublicPair = Pair;
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json"),
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.PublicPair", importedDocument.PackageImageFacts!.TypeAliases.Keys);

            var corruptedSourceText = importedDocument.ParseResult.SourceText.Replace(
                "public alias PublicPair = Pair;",
                "public alias PublicPair = Missing;",
                StringComparison.Ordinal);
            Assert.NotEqual(importedDocument.ParseResult.SourceText, corruptedSourceText);

            var corruptedDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(corruptedSourceText)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        stack Facade.PublicPair pair = new Facade.Pair() { Left = 3, Right = 4 };
                        return pair.Left;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.Contains("Facade.PublicPair", typeCheckModel.TypeAliases.Keys);
            Assert.Equal("Facade.Pair", typeCheckModel.TypeAliases["Facade.PublicPair"].TargetType.DisplayName);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedAliasDoctrineAndTraitImportsResolveFromPackageImageFactsWhenImportedParseTreeIsEmpty()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-empty-parse-import-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Pair {
                    i32[min max] Left;
                    i32[min max] Right;
                }

                public alias PublicPair = Pair;

                public doctrine Numbers {
                    finite law i32[min max] Double(i32[min max] value) {
                        return value + value;
                    }
                }

                public trait Comparable {
                    law i32[min max] Compare(i32[min max] other);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            var resolvedPackageModule = new ResolvedPackageModule(
                Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json"),
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.PublicPair", importedDocument.PackageImageFacts!.TypeAliases.Keys);
            Assert.Contains("Facade.Numbers.Double", importedDocument.PackageImageFacts.FunctionSignatures.Keys);
            Assert.Contains("Facade.Comparable.Compare", importedDocument.PackageImageFacts.FunctionSignatures.Keys);

            var emptyParseDocument = importedDocument with
            {
                ParseResult = StarkSyntax.ParseCompilationUnit(
                    """
                    module Facade
                    """)
            };

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        stack Facade.PublicPair pair = new Facade.Pair() { Left = 3, Right = 4 };
                        return Facade.Numbers.Double(pair.Left);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new DocumentOnlyModuleResolver(emptyParseDocument),
                    StopAfterPassId: "type-check"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);

            Assert.Contains("Facade.PublicPair", typeCheckModel.TypeAliases.Keys);
            Assert.Equal("Facade.Pair", typeCheckModel.TypeAliases["Facade.PublicPair"].TargetType.DisplayName);

            Assert.True(typeCheckModel.NamedTypes.TryGetValue("Facade.Numbers", out var doctrineType));
            Assert.NotNull(doctrineType);
            Assert.Equal(DeclarationKind.Doctrine, doctrineType.Kind);
            Assert.True(typeCheckModel.Functions.ContainsKey("Facade.Numbers.Double"));

            Assert.True(typeCheckModel.NamedTypes.TryGetValue("Facade.Comparable", out var traitType));
            Assert.NotNull(traitType);
            Assert.Equal(DeclarationKind.Trait, traitType.Kind);
            Assert.True(typeCheckModel.Functions.ContainsKey("Facade.Comparable.Compare"));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ManifestBackedInternalTypeAliasesRemainHiddenFromConsumers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-internal-alias-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                internal alias Hidden = i32[min max];

                public finite law i32[min max] Id(i32[min max] value) {
                    return value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(facadeModule.SourceSurface?.TypeAliases is null || facadeModule.SourceSurface.TypeAliases.Count == 0);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        stack Facade.Hidden value = 3;
                        return Facade.Id(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.False(consumerResult.Succeeded);
            Assert.Contains(
                consumerResult.Diagnostics,
                static diagnostic => diagnostic.Code == "STK3004"
                    && diagnostic.Message.Contains("Facade.Hidden", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }


    [Fact]
    public void ParseErrorsPreventLaterPassesFromRunning()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            public fn void Run();
            """));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "STK1000");
        Assert.False(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? _));

        Assert.Equal(PassExecutionStatus.Executed, result.Executions[0].Status);
        Assert.All(
            result.Executions.Skip(1),
            static execution => Assert.Equal(PassExecutionStatus.Skipped, execution.Status));
        Assert.Contains(
            result.Logs,
            log => log.Severity == DiagnosticSeverity.Warning
                && log.Category == "pipeline"
                && log.EventId == "pass-skipped"
                && log.Stage == "syntax-model");
    }


    [Fact]
    public void ManifestBackedTypeDestructorsRoundTripWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-destructor-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Counter {
                    i32[min max] Value;

                    mut drop {
                        self.Value = 0;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var counterType = Assert.Single(facadeModule.SourceSurface!.Types!, static type => type.Name == "Counter");
            Assert.NotNull(counterType.Destructor);
            Assert.True(counterType.Destructor!.IsMutable);
            Assert.Contains("self.Value = 0;", counterType.Destructor.BodyText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    unsafe fn i32[min max] Run() {
                        stack mut Facade.Counter value = new Facade.Counter() { Value = 1 };
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);

            var importedCounter = Assert.Single(importedModule.SyntaxModel.Declarations, static declaration => declaration.Name == "Counter");
            Assert.NotNull(importedCounter.Destructor);
            Assert.True(importedCounter.Destructor!.IsMutable);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }
}
