using Stark.Compiler;
using Stark.Parsing;

namespace compiler.Tests;

public sealed class CompilerPipelineTests
{
    private static IReadOnlyList<System.Numerics.BigInteger> CollectMirIntegerConstants(MidLevelIrFunction function)
    {
        var values = new List<System.Numerics.BigInteger>();

        foreach (var block in function.Blocks)
        {
            foreach (var statement in block.Statements)
            {
                if (statement.Value is null)
                {
                    continue;
                }

                switch (statement.Value)
                {
                    case MidLevelIrUseRValue { Operand: MidLevelIrIntegerConstantOperand integerOperand }:
                        values.Add(integerOperand.Value);
                        break;
                    case MidLevelIrConvertRValue { Operand: MidLevelIrIntegerConstantOperand convertedOperand }:
                        values.Add(convertedOperand.Value);
                        break;
                    case MidLevelIrInsertFieldRValue { Value: MidLevelIrIntegerConstantOperand fieldOperand }:
                        values.Add(fieldOperand.Value);
                        break;
                }
            }

            if (block.Terminator.Value is MidLevelIrIntegerConstantOperand operand)
            {
                values.Add(operand.Value);
            }
        }

        return values;
    }

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
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
        Assert.NotNull(abiModel);

        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("ModuleID = 'Demo'", llvmModule.Text);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssaModule));
        Assert.NotNull(ssaModule);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsaModule));
        Assert.NotNull(optimizedSsaModule);
        Assert.Equal(24, result.Executions.Count(static execution => execution.Status == PassExecutionStatus.Executed));
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

            fn i32 Run(bool flag, i32 limit) {
                stack i32 sum = 0;
                stack i32 i = 0;

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
        Assert.DoesNotContain(ssaFunction.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Switch);
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

            fn i32 Run() {
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

            fn i32 Run(i32 input) {
                stack mut i32 value = input;
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

                fn i32 Run(bool flag) {
                    stack mut i32 value = 0;
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
    public void PipelineFoldsImportedConstantLawCallsAcrossMirSsaAndLlvm()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Lib
                module Demo

                fn i32 Run() {
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

                        public finite law i32 Adjust(i32 value) {
                            stack mut i32 current = value;
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

            public finite law i32 Add(i32 left, i32 right);
            public strictfp finite law f32 Precise(f32 left, f32 right);
            export ffi cold fn void Accept(rawptr<i8> value);
            internal hot fn i32 Warm(i32 value);
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
        Assert.False(accept.NoUnwind);
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

                fn i32 Run() {
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

                        public ffi asm(x86_64) fn i64 Syscall3(i64 number, i64 arg1, i64 arg2, i64 arg3)
                            in("rax") number,
                            in("rdi") arg1,
                            in("rsi") arg2,
                            in("rdx") arg3,
                            out("rax") return,
                            clobber("rcx", "r11")
                        {
                            "syscall"
                        }

                        public ffi asm(aarch64) fn i64 Syscall3(i64 number, i64 arg1, i64 arg2, i64 arg3)
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

                public asm(x86_64) fn i64 MissingFfi(i64 number)
                    out("rax") return
                {
                    "syscall"
                }

                public ffi asm(aarch64) fn i64 NoMatch(i64 number)
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

                public ffi asm(x86_64) fn i64 Syscall0()
                    out("rax") return
                {
                    "syscall"
                }

                public ffi asm(x86_64) fn i64 Syscall0()
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

                public ffi asm(x86_64) fn i64 Broken(i64 number, out i64 result)
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

                public ffi asm(x86_64) fn i64 Syscall2(i64 number, rawptr<i8> path)
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
        Assert.False(effects.NoUnwind);
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

                fn i64 Run(rawptr<i8> path) {
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

                        public ffi asm(x86_64) fn i64 Syscall2(i64 number, rawptr<i8> path)
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
                i64 A;
                i64 B;
                i64 C;
            }

            fn Big Make() {
                return new Big() { A = 1, B = 2, C = 3 };
            }

            fn i64 Read(Big value) {
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
    public void MonomorphizedGenericFunctionsReceiveExplicitAbiLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Big {
                    i64 A;
                    i64 B;
                    i64 C;
                }

                fn T Bounce<T>(T value) {
                    return value;
                }

                fn i32 Run(i32 value) {
                    return Bounce(value);
                }

                fn Big Make(Big value) {
                    return Bounce(value);
                }
                """),
            new CompilerOptions(
                QualifyModuleSymbols: true,
                StopAfterPassId: "lower-abi"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
        Assert.NotNull(abiModel);

        var small = abiModel.Functions["__stark_mono_fn_Demo__Bounce__i32"];
        Assert.Equal("__stark_mono_fn_Demo__Bounce__i32", small.SymbolName);
        Assert.True(small.UsesFastCallingConvention);
        Assert.False(small.ReturnsIndirect);
        var smallParameter = Assert.Single(small.UserParameters);
        Assert.Equal(AbiParameterKind.Direct, smallParameter.Kind);
        Assert.Equal(StarkTypeKind.Integer, smallParameter.LlvmType.Kind);

        var big = abiModel.Functions["__stark_mono_fn_Demo__Bounce__Big"];
        Assert.Equal("__stark_mono_fn_Demo__Bounce__Big", big.SymbolName);
        Assert.True(big.UsesFastCallingConvention);
        Assert.True(big.ReturnsIndirect);
        var bigParameter = Assert.Single(big.UserParameters);
        Assert.Equal(AbiParameterKind.IndirectIn, bigParameter.Kind);
        Assert.Equal(StarkTypeKind.RawPointer, bigParameter.LlvmType.Kind);
    }

    [Fact]
    public void PlainFnsRefineToStrongerEffectProfilesFromSemanticAnalysis()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            struct Box {
                i32 Value;
            }

            fn i32 Add(i32 left, i32 right) {
                return left + right;
            }

            fn i32 Read(borrow Box box) {
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
    public void UnsupportedMirLoweringProducesStructuredWarningLogs()
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

        Assert.True(result.Succeeded);
        var log = Assert.Single(result.Logs, log =>
            log.Category == "lowering"
            && log.EventId == "unsupported-lowering"
            && log.Stage == "lower-mir"
            && log.SymbolName == "Run"
            && log.Operation == "LowerPostfixExpression");

        Assert.Equal(DiagnosticSeverity.Warning, log.Severity);
        Assert.Equal(CompilerLogKind.Gap, log.Kind);
        Assert.Equal(CompilerLogOutcome.Unsupported, log.Outcome);
        Assert.NotNull(log.Data);
        Assert.Equal("Demo", log.Data["module"]);
        Assert.Equal("Direct MIR lowering stopped in 'LowerPostfixExpression'.", log.Message);
        Assert.Equal("lower-postfix-expression", log.Data["feature"]);
        Assert.Equal("StarkCfg", log.Data["bodyLoweringKind"]);
    }

    [Fact]
    public void EmitLlvmModesConvertUnsupportedMirLoweringIntoStableDiagnostics()
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
            diagnostic => diagnostic.Code == "STK5000"
                && diagnostic.Stage == "lower-mir"
                && diagnostic.Message.Contains("Code generation does not yet support this construct (lower-postfix-expression).", StringComparison.Ordinal));
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

                static mut i32 Counter = 0;

                fn void Bump(i32 value) {
                    Counter = Counter + value;
                    return;
                }

                struct Resource {
                    i32 Value;

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

                export ffi fn i32 main() {
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
    public void HeapAllocatorLoweringAvoidsLlvmFallbackLogs()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Run() {
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
        Assert.Contains("call ptr @malloc(i64", llvmModule.Text);
        Assert.Contains("call void @free(ptr %slot_box)", llvmModule.Text);
    }

    [Fact]
    public void ClosedWorldModulePrivateLawHelpersInferAlwaysInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            fn i32 Add(i32 left, i32 right) {
                return left + right;
            }

            law i32 Use() {
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
        Assert.Equal(InlinePreference.InlineHint, use.InlinePreference);
    }

    [Fact]
    public void ClosedWorldLawInliningRespectsExplicitHintsAndSkipsRecursiveHelpers()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            inlinehint fn i32 Hint(i32 value) {
                return value + 1;
            }

            noinline fn i32 Stop(i32 value) {
                return value + 1;
            }

            fn i32 Loop(i32 value) {
                if (value == 0) {
                    return 0;
                }

                return Loop(value - 1);
            }

            law i32 Use(i32 value) {
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

                fn i32 Run() {
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

                        law i32 LawOnly() {
                            return 1;
                        }

                        law i32 LawBlocked() {
                            return 2;
                        }

                        public law i32 UseLaw() {
                            return LawOnly();
                        }

                        public fn i32 UseFn() {
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

                law i32 Run() {
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

                        law i32 LawOnly() {
                            return 1;
                        }

                        public law i32 UseLaw() {
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

                law i32 LawRun() {
                    return Math.UseLaw();
                }

                fn i32 FnRun() {
                    Touch();
                    return Math.UseLaw();
                }

                ffi fn void Touch();
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(
                ModuleResolver: new InMemoryModuleResolver(
                [
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public law i32 UseLaw() {
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

                law i32 Run() {
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

                        law i32 LawOnly() {
                            return 1;
                        }

                        public law i32 UseLaw() {
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

                fn i32 Run() {
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

                        public finite law i32 Add(i32 left, i32 right) {
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

                fn i32 Run() {
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

                        public finite law i32 Add(i32 left, i32 right) {
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

                        ffi fn i32 fputs(ascii text, rawptr<i8> stream);
                        const rawptr<i8> stdout = null;

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
                finite law i32 Add(i32 left, i32 right) {
                    return left + right;
                }
            }

            fn i32 Run() {
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
                law i32 Compare(i32 other);
            }

            fn i32 Run() {
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
    public void StructAndRecordDestructorsFlowIntoSyntaxModel()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Buffer {
                    i32 Value;

                    drop {
                        ;
                    }
                }

                record Cursor(i32 Position) {
                    mut drop {
                        self.Position = 0;
                    }
                }
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var buffer = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Struct && declaration.Name == "Buffer");
        Assert.NotNull(buffer.Destructor);
        Assert.False(buffer.Destructor!.IsMutable);

        var cursor = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Record && declaration.Name == "Cursor");
        Assert.NotNull(cursor.Destructor);
        Assert.True(cursor.Destructor!.IsMutable);
    }

    [Fact]
    public void TypeAliasDeclarationsFlowIntoSyntaxModel()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public alias Byte = i8;
                alias BufferView<T> = borrow T[];
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var byteAlias = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.TypeAlias && declaration.Name == "Byte");
        Assert.Equal(StarkVisibility.Public, byteAlias.Visibility);
        Assert.NotNull(byteAlias.TypeAlias);
        Assert.Equal("i8", byteAlias.TypeAlias!.AliasedType);
        Assert.Empty(byteAlias.TypeAlias.GenericParameters);

        var bufferViewAlias = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.TypeAlias && declaration.Name == "BufferView");
        Assert.Equal(StarkVisibility.Module, bufferViewAlias.Visibility);
        Assert.NotNull(bufferViewAlias.TypeAlias);
        Assert.Equal("borrowT[]", bufferViewAlias.TypeAlias!.AliasedType);
        Assert.Equal(["T"], bufferViewAlias.TypeAlias.GenericParameters);
    }

    [Fact]
    public void GenericFunctionDeclarationsCarryTypeParametersIntoSyntaxModel()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public fn T Identity<T>(T value) {
                    return value;
                }

                struct Box {
                    fn T Echo<T>(T value) {
                        return value;
                    }
                }

                trait Reader {
                    law T Read<T>(T value);
                }

                doctrine Projector {
                    law T Project<T>(T value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "syntax-model"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SyntaxModel, out SyntaxModel? syntaxModel));
        Assert.NotNull(syntaxModel);

        var identity = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Identity");
        Assert.NotNull(identity.Function);
        Assert.True(identity.Function!.IsGeneric);
        Assert.Equal(["T"], identity.Function.GenericParams);

        var echo = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Box.Echo");
        Assert.NotNull(echo.Function);
        Assert.True(echo.Function!.IsGeneric);
        Assert.Equal(["T"], echo.Function.GenericParams);

        var read = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Reader.Read");
        Assert.NotNull(read.Function);
        Assert.True(read.Function!.IsGeneric);
        Assert.Equal(["T"], read.Function.GenericParams);

        var project = Assert.Single(syntaxModel.Declarations, static declaration => declaration.Name == "Projector.Project");
        Assert.NotNull(project.Function);
        Assert.True(project.Function!.IsGeneric);
        Assert.Equal(["T"], project.Function.GenericParams);
    }

    [Fact]
    public void UnusedTypeAliasesDoNotBlockTheCurrentPipeline()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Demo

            alias Byte = i8;

            fn i32 Run() {
                return 0;
            }
            """));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        Assert.Contains("define fastcc i32 @Run()", llvmModule.Text);
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

                fn i32 Run() {
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
                            finite law i32 Add(i32 left, i32 right) {
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

                fn i32 Run() {
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
                            law i32 Compare(i32 other);
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
                    finite law i32 Add(i32 left, i32 right) {
                        return left + right;
                    }
                }

                public trait Comparable {
                    law i32 Compare(i32 other);
                }

                law i32 UseLaw() {
                    return Math.Numbers.Add(1, 2);
                }

                fn i32 UseFn() {
                    Touch();
                    return Math.Numbers.Add(3, 4);
                }

                ffi fn void Touch();
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
                            finite law i32 Add(i32 left, i32 right) {
                                return left + right;
                            }
                        }

                        public trait Comparable {
                            law i32 Compare(i32 other);
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
    public void PrivateTransitiveImportsDoNotBecomeVisibleToTheRootModule()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                fn i32 Run() {
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

                        public fn i32 Double(i32 value) {
                            return Math.Add(value, value);
                        }
                        """,
                        "/virtual/Facade.stark"
                    ),
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32 Add(i32 left, i32 right) {
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

                fn i32 Run() {
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

                        public fn i32 Double(i32 value) {
                            return Math.Add(value, value);
                        }
                        """,
                        "/virtual/Facade.stark"
                    ),
                    (
                        new ResolvedModuleReference("Math", "/virtual/Math.stark", IsExternal: false),
                        """
                        module Math

                        public finite law i32 Add(i32 left, i32 right) {
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

                    public ffi asm(x86_64) fn i64 Syscall0(i64 number)
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

                    fn i64 Run() {
                        return Syscall.Syscall0(39);
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

                    public ffi asm(x86_64) fn i64 Syscall0(i64 number)
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

                    fn i64 Run() {
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
                    finite law i32 Double(i32 value) {
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

                    fn i32 Run() {
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
                    finite law i32 Double(i32 value) {
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
                "finite law i32 Double(i32 value);",
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

                    fn i32 Run() {
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
                    law i32 Compare(i32 other);
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

                    fn i32 Run() {
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
                    law i32 Compare(i32 other);
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
                "law i32 Compare(i32 other);",
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

                    fn i32 Run() {
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
                    finite law i32 Add(i32 left, i32 right) {
                        return left + right;
                    }
                }

                public trait Comparable {
                    law i32 Compare(i32 other);
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

                    fn i32 Run() {
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
                    Integer(i32),
                    Move { X: i32, Y: i32 },
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
                    Err(i32),
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

                    finite law i32 Unwrap(Facade.IOResult<i32> result) {
                        switch (result) {
                            case Facade.IOResult<i32>.Ok(var value):
                                return value;
                            case Facade.IOResult<i32>.Err(var code):
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
    public void ManifestBackedGenericEnumsRecordTypeInstantiationTriggersWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-enum-trigger-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum IOResult<T> {
                    Ok(T),
                    Err(i32),
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    finite law i32 Unwrap(Facade.IOResult<i32> result) {
                        switch (result) {
                            case Facade.IOResult<i32>.Ok(var value):
                                return value;
                            case Facade.IOResult<i32>.Err(var code):
                                return code;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.Contains(typeCheckModel.TypeTriggers, static trigger => trigger.TypeName == "Facade.IOResult<i32>");
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
                i32 Value;
            }

            public const i32 Answer = 42;
            internal static rawptr<i8> Buffer = null;

            fn i32 Run() {
                stack Widget widget = new Widget() { Value = 1 };
                stack i32 value = widget.Value + 2;
                return value;
            }
            """));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);

        Assert.True(typeCheckModel.NamedTypes.ContainsKey("Widget"));
        Assert.True(typeCheckModel.Globals.ContainsKey("Answer"));
        Assert.Equal("i32", typeCheckModel.Functions["Run"].ReturnType.DisplayName);
        Assert.Contains(typeCheckModel.Literals, literal => literal.LiteralText == "42" && literal.Type.DisplayName == "i8[42 42]");
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
                Integer(i32),
                Move { X: i32, Y: i32 },
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
                Integer(i32),
                Move { X: i32, Y: i32 },
            }

            fn Token Make(i32 value) {
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

            fn i32 Run() {
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

            public const i32 Value = null;
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
                    finite law i32 Add(i32 left, i32 right) {
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
                i32 Value;

                fn i32 Scale(borrow Buffer self, i32 factor) {
                    return self.Value * factor;
                }

                fn i32 Scale(borrow Buffer self, bool doubleIt) {
                    if (doubleIt) {
                        return self.Value * 2;
                    }

                    return self.Value;
                }
            }

            fn i32 Run() {
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

                public finite law i32 Parse(i32 value) {
                    return value;
                }

                public finite law i32 Parse(bool value) {
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

                    fn i32 Run() {
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
                public fn Pair<i32> First(BufferView<i32> view);
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
                public alias IntBufferView = Pair<i32>[];
                public record Holder(IntBufferView View) {
                    IntBufferView Cached;

                    fn IntBufferView Echo(IntBufferView value);
                }

                public fn BufferView<i32> First(BufferView<i32> view);
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
            Assert.Equal("Pair<i32>[]", intBufferView.TargetType);

            var first = Assert.Single(sourceSurface.Functions!, static function => function.Name == "First");
            Assert.Equal("BufferView<i32>", first.ReturnType);
            var parameter = Assert.Single(first.Parameters);
            Assert.Equal("BufferView<i32>", parameter.Type);
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
                        TargetType: "i32[]")
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
        Assert.Contains("public alias BufferView = i32[];", sourceText, StringComparison.Ordinal);
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
                        ReturnType: "i32",
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
                        TargetType: "i32[]")
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
        Assert.Contains("public alias Buffer = i32[];", sourceText, StringComparison.Ordinal);
        Assert.Contains("public fn i32 Right();", sourceText, StringComparison.Ordinal);
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
                    ReturnType: new StarkPackageTypeReference("integer", BitWidth: 32),
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

        Assert.Contains("public fn i32 Right();", sourceText, StringComparison.Ordinal);
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
    public void PackageImageSourceBridgeUsesSourceSurfaceOverloadKeysForUnsupportedGenericTemplateBodiesWhenTypedInterfaceIsPresent()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-bridge-surface-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public alias Count = i32;

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

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        Path.Combine(tempDirectory.FullName, "Facade.starkpkg.json"),
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public alias Count = i32;", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn i32 Identity<T>(i32 value) {", sourceText, StringComparison.Ordinal);
            Assert.Contains("return value + 0;", sourceText, StringComparison.Ordinal);

            Assert.DoesNotContain("public fn i32 Identity<T>(i32 value);", sourceText, StringComparison.Ordinal);
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
    public void ManifestBackedTypedTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack T copy = value;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return copy;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run() {
                        stack i32 value = 7;
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var identity = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(identity.HasBody);
            Assert.True(identity.SupportsDirectCodeGeneration);
            Assert.Contains(identity.Locals, static local => local.Name == "copy");
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
    public void ManifestBackedTypedGroupedLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-grouped-local-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 SumTo<T>(i32 limit, T tag) {
                    stack mut i32 total = 0, stop = limit;
                    for willexit (stack mut i32 index = 0, max = stop; index < max; index += 1) {
                        total += index;
                    }

                    return total;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
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
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 SumTo<T>(i32 limit, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack mut i32 total = 0, stop = limit;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (stack mut i32 index = 0, max = stop;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 limit) {
                        stack i32 tag = 0;
                        return Facade.SumTo(limit, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumTo = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_SumTo__i32");
            Assert.True(sumTo.HasBody);
            Assert.True(sumTo.SupportsDirectCodeGeneration);
            Assert.Contains(sumTo.Locals, static local => local.Name == "total");
            Assert.Contains(sumTo.Locals, static local => local.Name == "stop");
            Assert.Contains(sumTo.Locals, static local => local.Name == "index");
            Assert.Contains(sumTo.Locals, static local => local.Name == "max");
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
    public void ManifestBackedTypedUninitializedLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-uninitialized-local-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 Observe<T>(i32 value, T tag) {
                    stack mut i32 current;
                    current = value;
                    return current;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
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
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 Observe<T>(i32 value, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack mut i32 current;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("current = value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack i32 tag = 0;
                        return Facade.Observe(value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "current");
            Assert.Contains(
                observe.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.Text == "current = value");
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
    public void ManifestBackedTypedDiscardedExpressionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-expression-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 Observe<T>(i32 value, T tag) {
                    value + 1;
                    return value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
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
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 Observe<T>(i32 value, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("value + 1;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack i32 tag = 0;
                        return Facade.Observe(value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(
                observe.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.Kind == MidLevelIrStatementKind.Evaluate
                    && statement.Text == "value + 1");
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
    public void ManifestBackedTypedConversionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-conversion-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 TruncateTyped<T>(f32 value, T tag) {
                    return (i32)value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 TruncateTyped<T>(f32 value, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return (i32)value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(f32 value) {
                        return Facade.TruncateTyped(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var truncate = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_TruncateTyped__", StringComparison.Ordinal));
            Assert.True(truncate.HasBody);
            Assert.True(truncate.SupportsDirectCodeGeneration);
            Assert.Contains(
                truncate.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrConvertRValue
                {
                    TargetType.Kind: StarkTypeKind.Integer,
                    TargetType.BitWidth: 32
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
    public void ManifestBackedTypedAssignmentTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-assignment-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 AddViaAssign<T>(T tag, i32 left, i32 right) {
                    stack mut i32 sum = left;
                    sum = sum + right;
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 AddViaAssign<T>(T tag, i32 left, i32 right);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack mut i32 sum = left;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("sum = sum + right;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 left, i32 right) {
                        return Facade.AddViaAssign(0, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var addViaAssign = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_AddViaAssign__", StringComparison.Ordinal));
            Assert.True(addViaAssign.HasBody);
            Assert.True(addViaAssign.SupportsDirectCodeGeneration);
            Assert.Contains(addViaAssign.Locals, static local => local.Name == "sum" && local.IsMutable);
            Assert.Contains(
                addViaAssign.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "sum"
                    && statement.Value is MidLevelIrUseRValue { Operand: MidLevelIrLocalOperand { Name: var operandName } }
                    && operandName.Contains("bin", StringComparison.Ordinal));
            Assert.Contains(
                addViaAssign.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
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
    public void ManifestBackedTypedIfTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-if-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 ChooseBranch<T>(bool takeLeft, i32 left, i32 right, T tag) {
                    stack mut i32 result = 0;
                    if (takeLeft) {
                        result = left;
                    } else {
                        result = right;
                    }
                    return result;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 ChooseBranch<T>(bool takeLeft, i32 left, i32 right, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("if (takeLeft)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("result = left;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("result = right;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(bool takeLeft, i32 left, i32 right) {
                        return Facade.ChooseBranch(takeLeft, left, right, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var chooseBranch = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_ChooseBranch__", StringComparison.Ordinal));
            Assert.True(chooseBranch.HasBody);
            Assert.True(chooseBranch.SupportsDirectCodeGeneration);
            Assert.Contains(chooseBranch.Locals, static local => local.Name == "result" && local.IsMutable);
            Assert.Contains(chooseBranch.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                chooseBranch.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrUseRValue
                {
                    Operand: MidLevelIrIntegerConstantOperand { Value: var integerValue }
                } && integerValue == System.Numerics.BigInteger.Zero
                    || value is MidLevelIrConvertRValue
                    {
                        Operand: MidLevelIrIntegerConstantOperand { Value: var convertedIntegerValue }
                    } && convertedIntegerValue == System.Numerics.BigInteger.Zero);
            Assert.Contains(
                chooseBranch.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "result");
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
    public void ManifestBackedTypedWhileTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-while-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 SumTo<T>(i32 count, T tag) {
                    stack mut i32 index = 0;
                    stack mut i32 sum = 0;
                    while willexit (index < count) {
                        sum = sum + index;
                        index = index + 1;
                    }
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 SumTo<T>(i32 count, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("while willexit (index < count)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("sum = sum + index;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("index = index + 1;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 count) {
                        return Facade.SumTo(count, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumTo = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_SumTo__", StringComparison.Ordinal));
            Assert.True(sumTo.HasBody);
            Assert.True(sumTo.SupportsDirectCodeGeneration);
            Assert.Contains(sumTo.Locals, static local => local.Name == "index" && local.IsMutable);
            Assert.Contains(sumTo.Locals, static local => local.Name == "sum" && local.IsMutable);
            Assert.Contains(sumTo.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                sumTo.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.LessThan }
                    || value is MidLevelIrUseRValue { Operand: MidLevelIrLocalOperand { Name: var name } }
                        && name.Contains("bin", StringComparison.Ordinal));
            Assert.Contains(
                sumTo.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
            Assert.Contains(
                sumTo.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "sum");
            Assert.Contains(
                sumTo.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "index");
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
    public void ManifestBackedTypedForTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-for-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 SumFor<T>(i32 count, T tag) {
                    stack mut i32 sum = 0;
                    for willexit (stack mut i32 index = 0; index < count; index = index + 1) {
                        sum = sum + index;
                    }
                    return sum;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 SumFor<T>(i32 count, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (stack mut i32 index = 0; index < count; index = index + 1)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("sum = sum + index;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 count) {
                        return Facade.SumFor(count, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumFor = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_SumFor__", StringComparison.Ordinal));
            Assert.True(sumFor.HasBody);
            Assert.True(sumFor.SupportsDirectCodeGeneration);
            Assert.Contains(sumFor.Locals, static local => local.Name == "sum" && local.IsMutable);
            Assert.Contains(sumFor.Locals, static local => local.Name == "index" && local.IsMutable);
            Assert.Contains(sumFor.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                sumFor.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.LessThan });
            Assert.Contains(
                sumFor.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
            Assert.Contains(
                sumFor.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "sum");
            Assert.Contains(
                sumFor.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "index");
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
    public void ManifestBackedTypedLoopControlTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-loop-control-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag) {
                    stack mut i32 sum = 0;
                    for willexit (stack mut i32 index = 0; index < count; index = index + 1) {
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

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("continue;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("break;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("for willexit (stack mut i32 index = 0; index < count; index = index + 1)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 count, i32 stopAt) {
                        return Facade.SumForControl(count, stopAt, 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var sumForControl = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_SumForControl__", StringComparison.Ordinal));
            Assert.True(sumForControl.HasBody);
            Assert.True(sumForControl.SupportsDirectCodeGeneration);
            Assert.Contains(sumForControl.Locals, static local => local.Name == "sum" && local.IsMutable);
            Assert.Contains(sumForControl.Locals, static local => local.Name == "index" && local.IsMutable);
            Assert.Contains(sumForControl.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(sumForControl.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Goto);
            Assert.Contains(
                sumForControl.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Equal });
            Assert.Contains(
                sumForControl.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "sum");
            Assert.Contains(
                sumForControl.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "index");
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

                public fn i32 SumWhileControl<T>(i32 count, i32 stopAt, T tag) {
                    stack mut i32 sum = 0;
                    stack mut i32 index = 0;
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

                public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag) {
                    stack mut i32 sum = 0;
                    for willexit (stack mut i32 index = 0; index < count; index = index + 1) {
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
            Assert.Contains("public fn i32 SumWhileControl<T>(i32 count, i32 stopAt, T tag);", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag);", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
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
    public void ManifestBackedTypedGenericMethodBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-method-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32 Dummy) {
                    fn T Echo<T>(borrow Box self, T value) {
                        stack T copy = value;
                        return copy;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        manifest,
                        facadeModule),
                    out var sourceText));

            Assert.Contains("fn T Echo<T>(borrow Box self, T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack T copy = value;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return copy;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Box box = new Facade.Box(1);
                        return box.Echo(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(
                mir.Functions,
                static function => function.Name.Contains("Echo", StringComparison.Ordinal)
                    && function.Name.StartsWith("__stark_mono_fn_Demo__", StringComparison.Ordinal));
            Assert.True(specialized.HasBody);
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local.Name == "copy");
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.TargetName == "copy");

            var run = Assert.Single(mir.Functions, static function => function.Name == "Run");
            Assert.Contains(
                run.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                value => value is MidLevelIrCallRValue call
                    && string.Equals(call.FunctionName, specialized.Name, StringComparison.Ordinal));
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

            public alias BufferView = i32[];

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
                export cold ffi fn void Sink(rawptr<i8> value);
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
                    i64 A;
                    i64 B;
                    i64 C;
                }

                public fn Big Make();
                public fn i64 Read(Big value);
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
                    i8 Small;
                    i32 Value;
                }

                public enum Token {
                    End,
                    Move { X: i32, Y: i32 },
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
                    i32 Value;
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
    public void PackageManifestPublishesGroupedLocalDeclarationTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-grouped-local-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 SumPair<T>(i32 value, T tag) {
                    stack i32 first = value, second = value + 1;
                    return first + second;
                }

                public fn i32 SumTo<T>(i32 limit, T tag) {
                    stack mut i32 total = 0, stop = limit;
                    for willexit (stack mut i32 index = 0, max = stop; index < max; index += 1) {
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

                public fn void Observe<T>(i32 value, T tag) {
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

                public fn i32 Observe<T>(i32 value, T tag) {
                    stack mut i32 current, next = value + 1;
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

                public record Pair(i32 First, i32 Second) { }

                public fn i32 Observe<T>(i32 value, T tag) {
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
    public void PackageManifestPublishesAssignmentExpressionTypedTemplateBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-assignment-expression-template-body-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 Observe<T>(i32 value, T tag) {
                    stack mut i32 current = 1;
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

                public fn i32 Observe<T>(rawmutptr<i32> ptr, i32 value, T tag) {
                    stack mut i32 copy = *ptr;
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

                public record Buffer(i32 First, i32[4] Values) { }

                public fn rawmutptr<Buffer> Pick<T>(rawmutptr<Buffer> ptr, T tag) {
                    return ptr;
                }

                public fn i32 Observe<T>(rawmutptr<Buffer> ptr, i32 slot, i32 value, T tag) {
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
    public void PackageManifestRetainsGenericTemplateBodyTextWhenTypedSubsetCannotRepresentBody()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-template-body-fallback-pipeline-");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 Store<T>(i32 value, T tag) {
                    stack mut i32 current = 0;
                    return *(&current) = value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var template = Assert.Single(facadeModule.GenericTemplates!.Functions, static item => item.QualifiedResolvedName == "Facade.Store");

            Assert.Null(template.TypedBody);
            Assert.NotNull(template.BodyText);
            Assert.Contains("return *(&current) = value;", template.BodyText, StringComparison.Ordinal);
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
    public void ManifestBackedTypedRawPointerDereferenceTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-raw-pointer-deref-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 Observe<T>(rawmutptr<i32> ptr, i32 value, T tag) {
                    stack mut i32 copy = *ptr;
                    return *ptr += copy + value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
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
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 Observe<T>(rawmutptr<i32> ptr, i32 value, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return *ptr += copy + value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack mut i32 current = 5;
                        stack i32 tag = 0;
                        return Facade.Observe(&current, value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "copy");
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(observeStatements, static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect);
            Assert.Contains(
                observeStatements.Select(static statement => statement.Value),
                static value => value is MidLevelIrLoadIndirectRValue);
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
    public void ManifestBackedTypedProjectedRawPointerDereferenceTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-projected-raw-pointer-deref-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(i32 First, i32[4] Values) { }

                public fn rawmutptr<Buffer> Pick<T>(rawmutptr<Buffer> ptr, T tag) {
                    return ptr;
                }

                public fn i32 Observe<T>(rawmutptr<Buffer> ptr, i32 slot, i32 value, T tag) {
                    (*ptr).First += value;
                    return (*Pick(ptr, tag)).Values[slot] = (*ptr).First + value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
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
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 Observe<T>(rawmutptr<Buffer> ptr, i32 slot, i32 value, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("(*ptr).First += value;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("(*Pick(ptr, tag)).Values[slot] = (*ptr).First + value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack mut i32[4] values = { 10, 20, 30, 40 };
                        stack mut Facade.Buffer buffer = { First = 5, Values = values };
                        stack i32 tag = 0;
                        return Facade.Observe(&buffer, 2, value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.True(observeStatements.Count(static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect) >= 2);
            Assert.Contains(observeStatements, static statement => statement.Value is MidLevelIrFieldAddressRValue);
            Assert.Contains(observeStatements, static statement => statement.Value is MidLevelIrElementAddressRValue);
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
    public void ManifestBackedTypedAssignmentExpressionTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-assignment-expression-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 Observe<T>(i32 value, T tag) {
                    stack mut i32 current = 1;
                    return current += value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
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
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 Observe<T>(i32 value, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return current += value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack i32 tag = 0;
                        return Facade.Observe(value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "current");
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(
                observeStatements,
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.Text == "current += value");
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
    public void ManifestBackedTypedObjectInitializerLocalDeclarationTemplateBodiesDoNotRequireBridgeBodyTextForImportedGenericSpecialization()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-object-initializer-local-body-bridge-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair(i32 First, i32 Second) { }

                public fn i32 Observe<T>(i32 value, T tag) {
                    stack Pair pair = { First = value, Second = value + 1 };
                    return pair.First + pair.Second;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(libraryResult, libraryPath);
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates,
                            CompilerSections = new StarkPackageCompilerSectionsManifest(
                                TypedInterface: facadeModule.TypedInterface,
                                CompilerFacts: facadeModule.CompilerFacts,
                                GenericTemplates: facadeModule.GenericTemplates),
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
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(manifestPath, libraryPath, typedOnlyManifest, typedFacadeModule),
                    out var sourceText));

            Assert.Contains("public fn i32 Observe<T>(i32 value, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack Pair pair = { First = value, Second = value + 1 };", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack i32 tag = 0;
                        return Facade.Observe(value, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var observe = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Observe__i32");
            Assert.True(observe.HasBody);
            Assert.True(observe.SupportsDirectCodeGeneration);
            Assert.Contains(observe.Locals, static local => local.Name == "pair");
            var observeStatements = observe.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(
                observeStatements,
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.Text.Contains("First", StringComparison.Ordinal));
            Assert.Contains(
                observeStatements,
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.Text.Contains("Second", StringComparison.Ordinal));
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
    public void ManifestBackedModulesPreserveImportedFunctionEffectsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-effects-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public strictfp hot noinline finite law f32 Precise(f32 value);
                export cold ffi fn void Sink(rawptr<i8> value);
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts
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
            Assert.Contains("public strictfp finite law f32 Precise(f32 value);", sourceText, StringComparison.Ordinal);
            Assert.Contains("export ffi fn void Sink(rawptr<i8> value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn f32 Run(f32 value) {
                        return value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "function-effects"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.FunctionEffects, out FunctionEffectModel? effectModel));
            Assert.NotNull(effectModel);

            var precise = effectModel.Functions["Facade.Precise"];
            Assert.True(precise.IsStrictFp);
            Assert.True(precise.IsHot);
            Assert.False(precise.IsCold);
            Assert.True(precise.IsPure);
            Assert.Equal(InlinePreference.NoInline, precise.InlinePreference);
            Assert.True(precise.UseFastCallingConvention);

            var sink = effectModel.Functions["Facade.Sink"];
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
    public void ManifestBackedModulesPreservePublishedLayoutFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-layout-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Padded {
                    i8 Small;
                    i32 Value;
                }

                public enum Token {
                    End,
                    Move { X: i32, Y: i32 },
                }
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts
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
            Assert.Contains("public struct Padded {", sourceText, StringComparison.Ordinal);
            Assert.Contains("public enum Token {", sourceText, StringComparison.Ordinal);

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
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "enum-layout"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(importedModule.PackageImageFacts!.ConcreteLayouts.TryGetValue("Facade.Padded", out var paddedLayout));
            Assert.Equal(8, paddedLayout.SizeBytes);
            Assert.Equal(4, paddedLayout.AlignmentBytes);
            Assert.True(importedModule.PackageImageFacts.EnumLayouts.TryGetValue("Facade.Token", out var importedTokenLayout));
            Assert.Equal(EnumLayoutKind.DirectTag, importedTokenLayout.Kind);
            Assert.Equal("$tag", importedTokenLayout.TagField.Name);
            Assert.Equal("$Move_X", importedTokenLayout.OrderedFields[1].Name);
            Assert.Equal("$Move_Y", importedTokenLayout.OrderedFields[2].Name);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
            Assert.NotNull(enumLayoutModel);
            Assert.True(enumLayoutModel.Layouts.TryGetValue("Facade.Token", out var tokenLayout));
            Assert.Equal(EnumLayoutKind.DirectTag, tokenLayout.Kind);
            Assert.Equal("$tag", tokenLayout.TagField.Name);
            Assert.Equal("$Move_X", tokenLayout.OrderedFields[1].Name);
            Assert.Equal("$Move_Y", tokenLayout.OrderedFields[2].Name);
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
    public void ManifestBackedModulesPreservePublishedSemanticFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-semantics-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32 Value;
                }

                public fn retborrow Box Echo(retborrow Box value) {
                    return value;
                }

                public fn void Reset(borrow mut Box value) {
                    value.Value = 0;
                    return;
                }
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts
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
            Assert.Contains("Echo(", sourceText, StringComparison.Ordinal);
            Assert.Contains("Reset(", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Box value) {
                        return value.Value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "load-modules"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(importedModule.PackageImageFacts!.FunctionSemantics.TryGetValue("Facade.Echo", out var echo));
            Assert.NotNull(echo.MemoryEffects);
            Assert.True(echo.MemoryEffects!.CapturesArgumentMemory);
            Assert.Equal(ParameterCaptureKind.Return, Assert.Single(echo.Parameters!).CaptureKind);

            Assert.True(importedModule.PackageImageFacts.FunctionSemantics.TryGetValue("Facade.Reset", out var reset));
            Assert.NotNull(reset.MemoryEffects);
            Assert.True(reset.MemoryEffects!.WritesArgumentMemory);
            Assert.True(Assert.Single(reset.Parameters!).Writes);
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
    public void ManifestBackedSemanticValidationUsesPublishedBorrowFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-semantic-validation-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32 Value;
                }

                public fn void Touch(borrow mut Box value) {
                    value.Value = 0;
                    return;
                }
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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts
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
            Assert.Contains("Touch(", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Outer(borrow mut Facade.Box value) {
                        Facade.Touch(value);
                        return;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "semantic-validate"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
            Assert.NotNull(validation);

            var outer = validation.Functions["Outer"];
            Assert.NotNull(outer.MemoryEffects);
            Assert.True(outer.MemoryEffects!.WritesArgumentMemory);
            Assert.True(Assert.Single(outer.Parameters!).Writes);
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
                    i32 Count;
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
                    const i32 one = 1;
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
            Assert.Equal(32, localConstant.Type.BitWidth);
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

                public fn i32 TruncateTyped<T>(f32 value, T tag) {
                    return (i32)value;
                }

                public fn i32 AddViaAssign<T>(T tag, i32 left, i32 right) {
                    stack mut i32 sum = left;
                    sum = sum + right;
                    return sum;
                }

                public fn i32 ChooseBranch<T>(bool takeLeft, i32 left, i32 right, T tag) {
                    stack mut i32 result = 0;
                    if (takeLeft) {
                        result = left;
                    } else {
                        result = right;
                    }
                    return result;
                }

                public fn i32 SumTo<T>(i32 count, T tag) {
                    stack mut i32 index = 0;
                    stack mut i32 sum = 0;
                    while willexit (index < count) {
                        sum = sum + index;
                        index = index + 1;
                    }
                    return sum;
                }

                public fn i32 SumFor<T>(i32 count, T tag) {
                    stack mut i32 sum = 0;
                    for willexit (stack mut i32 index = 0; index < count; index = index + 1) {
                        sum = sum + index;
                    }
                    return sum;
                }

                public fn i32 SumForControl<T>(i32 count, i32 stopAt, T tag) {
                    stack mut i32 sum = 0;
                    for willexit (stack mut i32 index = 0; index < count; index = index + 1) {
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

                public fn i8 One<T>(T tag) {
                    return 1;
                }

                public fn bool NegateFlag<T>(T tag, bool flag) {
                    return !flag;
                }

                public fn i32 AddTagged<T>(T tag, i32 left, i32 right) {
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

                public record EchoBox(i32 Dummy) {
                    fn i32 Echo(borrow EchoBox self, i32 value) {
                        return value;
                    }
                }

                public fn EchoBox MakeEchoBox(i32 dummy) {
                    return new EchoBox(dummy);
                }

                public fn i32 CallEcho<T>(EchoBox box, i32 value, T tag) {
                    return box.Echo(value);
                }

                public struct EchoHolder {
                    EchoBox Box;
                }

                public fn i32 CallHeldEcho<T>(EchoHolder holder, i32 value, T tag) {
                    return holder.Box.Echo(value);
                }

                public fn i32 CallIndexedEcho<T>(EchoBox[] boxes, i32 index, i32 value, T tag) {
                    return boxes[index].Echo(value);
                }

                public fn i32 CallMadeEcho<T>(i32 value, T tag) {
                    return MakeEchoBox(1).Echo(value);
                }

                public struct IntBox {
                    i32 Value;
                }

                public fn IntBox MakeIntBox(i32 value) {
                    return new IntBox() { Value = value };
                }

                public fn i32 ReadMadeValue<T>(i32 value, T tag) {
                    return MakeIntBox(value).Value;
                }

                public fn i32 CallConstructedEcho<T>(i32 value, T tag) {
                    return new EchoBox(1).Echo(value);
                }

                public fn i32 ReadConstructedValue<T>(i32 value, T tag) {
                    return new IntBox() { Value = value }.Value;
                }

                public fn i32 ChooseBoxValue<T>(bool takeLeft, IntBox left, IntBox right, T tag) {
                    return (takeLeft ? left : right).Value;
                }

                public fn i32 ChooseEcho<T>(bool takeLeft, EchoBox left, EchoBox right, i32 value, T tag) {
                    return (takeLeft ? left : right).Echo(value);
                }

                public fn i32 ReadSliceAt<T>(i32[] view, i32 index, T tag) {
                    return view[index];
                }

                public fn ascii SliceAsciiWindow<T>(ascii text, i32 start, i32 length, T tag) {
                    return text[start, length];
                }

                public struct SliceBox<T> {
                    i32[] Values;
                }

                public fn i32 ReadBoxSliceAt<T>(SliceBox<T> box, i32 index, T tag) {
                    return box.Values[index];
                }

                public struct Counted<T> {
                    T Value;
                    i32 Count;
                }

                public fn i32 ReadIndexedCount<T>(Counted<T>[] pairs, i32 index, T tag) {
                    return pairs[index].Count;
                }

                public record ResetBox(i32 Value) {
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
                    Value { Data: T, Tag: i32 },
                }

                public fn Boxed<T> WrapNamed<T>(T value, i32 tag) {
                    return Boxed<T>.Value { Data: value, Tag: tag };
                }

                public fn Boxed<T> WrapNamedConst<T>(T value) {
                    return Boxed<T>.Value { Data: value, Tag: 1 };
                }

                public fn T Choose<T>(bool takeLeft, T left, T right) {
                    return takeLeft ? left : right;
                }

                public fn i32 MinTagged<T>(T tag, i32 left, i32 right) {
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
                    Value { Data: T, Tag: i32 },
                }

                public record Counter(i32 Value, i32 Count) { }

                public fn i32 HasValueSwitch<T>(Option<T> value) {
                    switch (value) {
                        case Option<T>.Some(var payload):
                            return 1;
                        case Option<T>.None:
                            return 0;
                    }
                }

                public fn i32 ReadTagSwitch<T>(Boxed<T> boxed) {
                    switch (boxed) {
                        case Boxed<T>.Value { Data: _, Tag: var tag }:
                            return tag;
                    }
                }

                public fn i32 ReadCountSwitch<T>(Counter counter, T tag) {
                    switch (counter) {
                        case Counter(_, var count):
                            return count;
                    }
                }

                public fn i32 ClassifySwitch<T>(i32 value, T tag) {
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

                public fn i32 Truncate<T>(f32 value, T tag) {
                    return (i32)value;
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
                    Value { Data: T, Tag: i8 },
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

                public fn i32 HasValue<T>(Option<T> value) {
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
                    Value { Data: T, Tag: i32 },
                }

                public fn i32 ReadTag<T>(Boxed<T> boxed) {
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

                public record Counter(i32 Value, i32 Count) { }

                public fn i32 ReadCount<T>(Counter counter, T tag) {
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

                public record Box(i32 Dummy) {
                    fn i32 Echo(borrow Box self, i32 value) {
                        return value;
                    }
                }

                public fn i32 Forward<T>(T tag, Box box, i32 value) {
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
                    i32 Tag;
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

                public fn i32 Forward<T>(T value, bool flag) {
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
    public void ManifestBackedGenericFunctionsMaterializeConcreteBodiesFromPackageImageTemplates()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.TypedInterface);
            Assert.NotNull(facadeModule.CompilerFacts);
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
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            var typedOnlyFacadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedOnlyFacadeModule),
                    out var sourceText));
            Assert.Contains("public fn i32 HasValue<T>(Option<T> value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("switch (value)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-hir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            var importedIdentity = Assert.Single(importedModule.SyntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Identity");
            Assert.True(importedIdentity.Function!.HasBody);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
            Assert.NotNull(hir);

            var monomorphized = Assert.Single(
                hir.Functions,
                static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(monomorphized.HasBody);
            Assert.Equal(FunctionBodyLoweringKind.StarkCfg, monomorphized.BodyLoweringKind);
            Assert.Equal("Facade.Identity", monomorphized.Signature.TemplateName);
            Assert.Equal("Facade.Identity", monomorphized.BodyTemplateName);
            Assert.NotNull(monomorphized.GenericTypeSubstitution);
            Assert.True(monomorphized.GenericTypeSubstitution!.TryGetValue("T", out var substitutedType));
            Assert.Equal("i32", substitutedType.DisplayName);
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
    public void ManifestBackedGenericBodiesCanConstructImportedPrimaryConstructorTypes()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-imported-primary-ctor-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32 Value) { }

                public fn i32 MakeFlag<T>(T value) {
                    stack Counter counter = new Counter(1);
                    return counter.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

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

                    fn i32 Run(i32 value) {
                        return Facade.MakeFlag(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_MakeFlag__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value" });
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
    public void ManifestBackedGenericBodiesUsePublishedLocalDeclarationTypesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-local-type-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Identity") with
            {
                BodyText = """
                    {
                        stack Missing copy = value;
                        return copy;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            var typedOnlyFacadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedOnlyFacadeModule),
                    out var sourceText));
            Assert.Contains("public fn i32 ReadTag<T>(Boxed<T> boxed);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("switch (boxed)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");
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
    public void ManifestBackedGenericBodiesPreferTypedTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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

                public fn T Forward<T>(T value) {
                    return Identity(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplates = facadeModule.GenericTemplates!.Functions
                .Select(template => template.QualifiedResolvedName switch
                {
                    "Facade.Identity" => template with
                    {
                        BodyText = """
                            {
                                return value;
                            }
                            """
                    },
                    "Facade.Forward" => template with
                    {
                        BodyText = """
                            {
                                return value;
                            }
                            """
                    },
                    _ => template
                })
                .ToArray();

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
                            GenericTemplates = new StarkPackageGenericTemplateSection(corruptedTemplates)
                        }
                        : module)
                    .ToArray()
            };

            var typedOnlyFacadeModule = WithEffectiveLegacyCompilerSectionCopies(
                Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedOnlyFacadeModule),
                    out var sourceText));
            Assert.Contains("public fn i32 ReadCount<T>(Counter counter, T tag);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("switch (counter)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack i32 identity = Facade.Identity(value);
                        return Facade.Forward(identity);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var identity = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(identity.SupportsDirectCodeGeneration);
            Assert.Contains(identity.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");

            var forward = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.True(forward.SupportsDirectCodeGeneration);
            Assert.Contains(
                forward.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "__stark_mono_fn_Demo__Facade_Identity__i32" });
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
    public void ManifestBackedGenericBodiesPreferTypedConstTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-const-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T ConstIdentity<T>(T value) {
                    const T copy = value;
                    return copy;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ConstIdentity") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.ConstIdentity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var constIdentity = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_ConstIdentity__", StringComparison.Ordinal));
            Assert.True(constIdentity.SupportsDirectCodeGeneration);
            Assert.Contains(
                constIdentity.Locals,
                static local => local.Name == "copy"
                    && local.Type.DisplayName == "i32"
                    && local.IsConstant);
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
    public void ManifestBackedGenericBodiesPreferTypedMultiLocalTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-multi-local-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }

                public fn T Relay<T>(T value) {
                    stack T copy = value;
                    stack T echoed = Identity(copy);
                    return echoed;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Relay") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Relay(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var relay = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_Relay__", StringComparison.Ordinal));
            Assert.True(relay.SupportsDirectCodeGeneration);
            Assert.Contains(relay.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");
            Assert.Contains(relay.Locals, static local => local.Name == "echoed" && local.Type.DisplayName == "i32");
            Assert.Contains(
                relay.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "__stark_mono_fn_Demo__Facade_Identity__i32" });
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
    public void ManifestBackedGenericBodiesPreferTypedConditionalTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-conditional-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Choose<T>(bool takeLeft, T left, T right) {
                    return takeLeft ? left : right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Choose") with
            {
                BodyText = """
                    {
                        return left;
                    }
                    """
            };

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
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(bool takeLeft, i32 left, i32 right) {
                        return Facade.Choose(takeLeft, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var choose = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Choose__i32");
            Assert.True(choose.SupportsDirectCodeGeneration);
            Assert.Contains(choose.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                choose.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.TargetName is not null
                    && statement.TargetName.Contains("typed_cond", StringComparison.Ordinal));
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
    public void ManifestBackedGenericBodiesPreferTypedBinaryTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-binary-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 AddTagged<T>(T tag, i32 left, i32 right) {
                    return left + right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.AddTagged") with
            {
                BodyText = """
                    {
                        return left;
                    }
                    """
            };

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
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 left, i32 right) {
                        return Facade.AddTagged(0, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var addTagged = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_AddTagged__", StringComparison.Ordinal));
            Assert.True(addTagged.SupportsDirectCodeGeneration);
            Assert.Contains(
                addTagged.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add });
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
    public void ManifestBackedGenericBodiesPreferTypedShortCircuitTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-short-circuit-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn bool Both<T>(T tag, bool left, bool right) {
                    return left && right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Both") with
            {
                BodyText = """
                    {
                        return right;
                    }
                    """
            };

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
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn bool Run(bool left, bool right) {
                        return Facade.Both(0, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var both = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_Both__", StringComparison.Ordinal));
            Assert.True(both.SupportsDirectCodeGeneration);
            Assert.Contains(both.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                both.Blocks.SelectMany(static block => block.Statements),
                static statement => statement.Kind == MidLevelIrStatementKind.Assign
                    && statement.TargetName is not null
                    && statement.TargetName.Contains("typed_and", StringComparison.Ordinal));
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
    public void ManifestBackedGenericBodiesPreferTypedComparisonConditionsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-comparison-condition-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 MinTagged<T>(T tag, i32 left, i32 right) {
                    return left < right ? left : right;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.MinTagged") with
            {
                BodyText = """
                    {
                        return right;
                    }
                    """
            };

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
                                facadeModule.GenericTemplates.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 left, i32 right) {
                        return Facade.MinTagged(0, left, right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var minTagged = Assert.Single(
                mir.Functions,
                static function => function.Name.StartsWith("__stark_mono_fn_Demo__Facade_MinTagged__", StringComparison.Ordinal));
            Assert.True(minTagged.SupportsDirectCodeGeneration);
            Assert.Contains(minTagged.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                minTagged.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.LessThan });
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
    public void ManifestBackedGenericImportsCanLoadFromExplicitCompilerSectionsWhenLegacyFieldsAreMissing()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-section-only-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.CompilerSections);

            var sectionOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            TypedInterface = null,
                            CompilerFacts = null,
                            GenericTemplates = null
                        }
                        : module)
                    .ToArray()
            };
            var sectionOnlyFacade = Assert.Single(sectionOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.Null(sectionOnlyFacade.TypedInterface);
            Assert.Null(sectionOnlyFacade.CompilerFacts);
            Assert.Null(sectionOnlyFacade.GenericTemplates);
            Assert.NotNull(sectionOnlyFacade.CompilerSections);

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        sectionOnlyManifest,
                        sectionOnlyFacade),
                    out var sourceText));
            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("stack T copy = value;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, sectionOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");
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
    public void ManifestBackedGenericImportsPreferExplicitCompilerSectionsOverConflictingLegacyFields()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-conflicting-sections-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var conflictingManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            TypedInterface = new StarkPackageTypedInterfaceSection(
                                Functions:
                                [
                                    new StarkPackageTypedFunctionManifest(
                                        Name: "Identity",
                                        QualifiedName: "Facade.Identity",
                                        Visibility: "public",
                                        SymbolName: "Facade.Identity",
                                        Kind: "fn",
                                        ReturnType: new StarkPackageTypeReference("integer", BitWidth: 8),
                                        Parameters:
                                        [
                                            new StarkPackageTypedParameterManifest(
                                                "value",
                                                new StarkPackageTypeReference("named", Name: "T"))
                                        ],
                                        IsFfi: false,
                                        IsStrictFp: false,
                                        UseFastCallingConvention: true,
                                        GenericParameters: new[] { "T" })
                                ],
                                Types: [],
                                Globals: []),
                            CompilerFacts = new StarkPackageCompilerFactsSection(
                                FunctionEffects: Array.Empty<StarkPackageFunctionEffectManifest>()),
                            GenericTemplates = new StarkPackageGenericTemplateSection(
                                Array.Empty<StarkPackageFunctionTemplateManifest>())
                        }
                        : module)
                    .ToArray()
            };
            var conflictingFacade = Assert.Single(conflictingManifest.Modules, static module => module.ModuleName == "Facade");

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        conflictingManifest,
                        conflictingFacade),
                    out var sourceText));
            Assert.Contains("public fn T Identity<T>(T value);", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("public fn i8 Identity<T>(T value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, conflictingManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-mir"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local.Name == "copy" && local.Type.DisplayName == "i32");
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
    public void ManifestBackedGenericBodiesPreferTypedFieldAccessTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-field-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T> {
                    T Value;
                }

                public fn T ReadValue<T>(Box<T> box, T fallback) {
                    return box.Value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadValue") with
            {
                BodyText = """
                    {
                        return fallback;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack i32 fallback = 0;
                        return Facade.ReadValue(new Facade.Box<i32>() { Value = value }, fallback);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadValue__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Value" });
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
    public void ManifestBackedGenericBodiesPreferTypedMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32 Dummy) {
                    fn i32 Echo(borrow Box self, i32 value) {
                        return value;
                    }
                }

                public fn i32 Forward<T>(Box box, i32 value, T tag) {
                    return box.Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("Forward<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return box.Echo(value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Box box = new Facade.Box(1);
                        return Facade.Forward(box, value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.Box.Echo" });
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
    public void ManifestBackedGenericBodiesPreferTypedChainedMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-chained-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record EchoBox(i32 Dummy) {
                    fn i32 Echo(borrow EchoBox self, i32 value) {
                        return value;
                    }
                }

                public struct EchoHolder {
                    EchoBox Box;
                }

                public fn i32 CallHeldEcho<T>(EchoHolder holder, i32 value, T tag) {
                    return holder.Box.Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.CallHeldEcho") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("CallHeldEcho<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("holder.Box.Echo(value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.EchoHolder holder = new Facade.EchoHolder() { Box = new Facade.EchoBox(1) };
                        return Facade.CallHeldEcho(holder, value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_CallHeldEcho__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.EchoBox.Echo" });
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
    public void ManifestBackedGenericBodiesPreferTypedDirectCallReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-direct-receiver-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record EchoBox(i32 Dummy) {
                    fn i32 Echo(borrow EchoBox self, i32 value) {
                        return value;
                    }
                }

                public fn EchoBox MakeEchoBox(i32 dummy) {
                    return new EchoBox(dummy);
                }

                public fn i32 CallMadeEcho<T>(i32 value, T tag) {
                    return MakeEchoBox(1).Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.CallMadeEcho") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("CallMadeEcho<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("MakeEchoBox(1).Echo(value)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.CallMadeEcho(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_CallMadeEcho__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.EchoBox.Echo" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.MakeEchoBox" });
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
    public void ManifestBackedGenericBodiesPreferTypedObjectCreationReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-object-receiver-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record EchoBox(i32 Dummy) {
                    fn i32 Echo(borrow EchoBox self, i32 value) {
                        return value;
                    }
                }

                public fn i32 CallConstructedEcho<T>(i32 value, T tag) {
                    return new EchoBox(1).Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.CallConstructedEcho") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("CallConstructedEcho<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("new EchoBox(1).Echo(value)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.CallConstructedEcho(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_CallConstructedEcho__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.EchoBox.Echo" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue);
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
    public void ManifestBackedGenericBodiesPreferTypedGroupedConditionalReceiverMemberCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-grouped-receiver-member-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record EchoBox(i32 Dummy) {
                    fn i32 Echo(borrow EchoBox self, i32 value) {
                        return value;
                    }
                }

                public fn i32 ChooseEcho<T>(bool takeLeft, EchoBox left, EchoBox right, i32 value, T tag) {
                    return (takeLeft ? left : right).Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ChooseEcho") with
            {
                BodyText = """
                    {
                        return value;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ChooseEcho<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("(takeLeft ? left : right).Echo(value)", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(bool takeLeft, i32 value) {
                        stack Facade.EchoBox left = new Facade.EchoBox(1);
                        stack Facade.EchoBox right = new Facade.EchoBox(2);
                        return Facade.ChooseEcho(takeLeft, left, right, value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ChooseEcho__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.EchoBox.Echo" });
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
    public void ManifestBackedGenericBodiesPreferTypedVoidDirectCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-void-direct-statement-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record ResetBox(i32 Value) { }

                public fn void ResetValue(borrow mut ResetBox box) {
                    box.Value = 0;
                }

                public fn void ForwardReset<T>(borrow mut ResetBox box, T tag) {
                    ResetValue(box);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ForwardReset") with
            {
                BodyText = """
                    {
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ForwardReset<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("ResetValue(box);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(borrow mut Facade.ResetBox box, i32 tag) {
                        Facade.ForwardReset(box, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ForwardReset__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.ResetValue" });
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
    public void ManifestBackedGenericBodiesPreferTypedVoidMemberCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-void-member-statement-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record ResetBox(i32 Value) {
                    fn void Reset(borrow mut ResetBox self) {
                        self.Value = 0;
                    }
                }

                public fn void ForwardMethodReset<T>(borrow mut ResetBox box, T tag) {
                    box.Reset();
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ForwardMethodReset") with
            {
                BodyText = """
                    {
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ForwardMethodReset<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("box.Reset();", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(borrow mut Facade.ResetBox box, i32 tag) {
                        Facade.ForwardMethodReset(box, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ForwardMethodReset__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.ResetBox.Reset" });
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
    public void ManifestBackedGenericBodiesPreferTypedConditionalCallStatementTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-conditional-call-statement-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record ResetBox(i32 Value) { }

                public fn void ResetValue(borrow mut ResetBox box, i32 next) {
                    box.Value = next;
                }

                public fn void SelectReset<T>(bool chooseLeft, borrow mut ResetBox left, borrow mut ResetBox right, T tag) {
                    chooseLeft ? ResetValue(left, 7) : ResetValue(right, 9);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.SelectReset") with
            {
                BodyText = """
                    {
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("SelectReset<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("chooseLeft ? ResetValue(left, 7) : ResetValue(right, 9);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(bool chooseLeft, borrow mut Facade.ResetBox left, borrow mut Facade.ResetBox right, i32 tag) {
                        Facade.SelectReset(chooseLeft, left, right, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_SelectReset__i32");
            Assert.True(
                specialized.SupportsDirectCodeGeneration,
                string.Join(
                    Environment.NewLine,
                    consumerResult.Logs.Select(static log => $"{log.Stage}:{log.Operation}:{log.Message}")));
            Assert.Contains(specialized.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);

            var resetCalls = specialized.Blocks
                .SelectMany(static block => block.Statements)
                .Select(static statement => statement.Value)
                .OfType<MidLevelIrCallRValue>()
                .Count(static value => value.FunctionName == "Facade.ResetValue");
            Assert.Equal(2, resetCalls);
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
    public void ManifestBackedGenericBodiesPreferTypedFieldAndIndexAssignmentTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-field-index-assignment-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(i32[] Values, i32 Count) { }

                public fn void WriteValue<T>(borrow mut Buffer buffer, i32 index, i32 next, T tag) {
                    buffer.Count = next;
                    buffer.Values[index] = next;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.WriteValue") with
            {
                BodyText = """
                    {
                        return;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("WriteValue<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("buffer.Count = next;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("buffer.Values[index] = next;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(borrow mut Facade.Buffer buffer, i32 index, i32 next, i32 tag) {
                        Facade.WriteValue(buffer, index, next, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_WriteValue__i32");
            Assert.True(
                specialized.SupportsDirectCodeGeneration,
                string.Join(
                    Environment.NewLine,
                    consumerResult.Logs.Select(static log => $"{log.Stage}:{log.Operation}:{log.Message}")));

            var statements = specialized.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue { FieldName: "Count" });
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue { FieldName: "Values" });
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrSliceElementAddressRValue);
            Assert.True(statements.Count(static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect) >= 2);
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
    public void ManifestBackedGenericBodiesPreferTypedCompoundFieldAndIndexAssignmentTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-compound-field-index-assignment-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Buffer(i32[] Values, i32 Count) { }

                public fn void AddValue<T>(borrow mut Buffer buffer, i32 index, i32 next, T tag) {
                    buffer.Count += next;
                    buffer.Values[index] += next;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.AddValue") with
            {
                BodyText = """
                    {
                        return;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("AddValue<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("buffer.Count += next;", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("buffer.Values[index] += next;", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(borrow mut Facade.Buffer buffer, i32 index, i32 next, i32 tag) {
                        Facade.AddValue(buffer, index, next, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_AddValue__i32");
            Assert.True(
                specialized.SupportsDirectCodeGeneration,
                string.Join(
                    Environment.NewLine,
                    consumerResult.Logs.Select(static log => $"{log.Stage}:{log.Operation}:{log.Message}")));

            var statements = specialized.Blocks.SelectMany(static block => block.Statements).ToArray();
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue { FieldName: "Count" });
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrFieldAddressRValue { FieldName: "Values" });
            Assert.Contains(statements, static statement => statement.Value is MidLevelIrSliceElementAddressRValue);
            Assert.True(statements.Count(static statement => statement.Value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Add }) >= 2);
            Assert.True(statements.Count(static statement => statement.Kind == MidLevelIrStatementKind.StoreIndirect) >= 2);
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
    public void ManifestBackedGenericBodiesPreferTypedIndexAccessTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-index-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 ReadSliceAt<T>(i32[] view, i32 index, T tag) {
                    return view[index];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadSliceAt") with
            {
                BodyText = """
                    {
                        return tag;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ReadSliceAt<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("view[index]", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 index, i32 tag) {
                        stack i32[3] values = { 4, 7, 9 };
                        stack i32[] view = values;
                        return Facade.ReadSliceAt(view, index, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadSliceAt__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrLoadIndirectRValue { Text: "view[index]" });
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
    public void ManifestBackedGenericBodiesPreferTypedFullViewTextSliceTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-full-view-text-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn ascii WholeAscii<T>(ascii text, T tag) {
                    return text[];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var wholeAscii = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.WholeAscii");
            Assert.NotNull(wholeAscii.TypedBody);
            var wholeAsciiReturn = Assert.Single(wholeAscii.TypedBody!.Statements);
            Assert.Equal("return", wholeAsciiReturn.Kind);
            Assert.Equal("index-access", wholeAsciiReturn.Expression.Kind);
            var wholeAsciiArguments = Assert.Single(wholeAsciiReturn.Expression.Arguments!);
            Assert.Equal("name", wholeAsciiArguments.Kind);
            Assert.Equal("text", wholeAsciiArguments.Name);

            var corruptedTemplate = wholeAscii with
            {
                BodyText = "{ return this is not valid Stark; }"
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            Assert.True(
                PackageImageLoader.TryBuildStructuredModuleDocument(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var importedDocument));
            Assert.DoesNotContain("this is not valid Stark", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn ascii WholeAscii<T>(ascii text, T tag);", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return text[];", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn ascii Run() {
                        return Facade.WholeAscii("hello", 0);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.DoesNotContain("this is not valid Stark", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.Contains("public fn ascii WholeAscii<T>(ascii text, T tag);", importedModule.ParseResult.SourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return text[];", importedModule.ParseResult.SourceText, StringComparison.Ordinal);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(
                mir.Functions,
                static function => function.Name.Contains("Facade_WholeAscii", StringComparison.Ordinal));
            Assert.True(specialized.SupportsDirectCodeGeneration);
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
    public void ManifestBackedGenericBodiesPreferTypedTextSliceTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-text-slice-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn ascii SliceAsciiWindow<T>(ascii text, i32 start, i32 length, T tag) {
                    return text[start, length];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.SliceAsciiWindow") with
            {
                BodyText = """
                    {
                        return text;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("SliceAsciiWindow<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("text[start, length]", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn ascii Run(i32 start, i32 length) {
                        return Facade.SliceAsciiWindow("hello", start, length, start);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_SliceAsciiWindow__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrTextSliceRValue textSlice
                    && textSlice.TextValue is MidLevelIrParameterOperand { Name: "text" }
                    && textSlice.Type.Kind == StarkTypeKind.Ascii
                    && textSlice.Start.Type.DisplayName == "i64"
                    && textSlice.Length.Type.DisplayName == "i64");
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
    public void ManifestBackedGenericBodiesPreferTypedSingleElementTextIndexTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-text-index-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn ascii PickAsciiUnit<T>(ascii text, i32 index, T tag) {
                    return text[index];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.PickAsciiUnit") with
            {
                BodyText = """
                    {
                        return text;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("PickAsciiUnit<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("text[index]", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn ascii Run(i32 index) {
                        return Facade.PickAsciiUnit("hello", index, index);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_PickAsciiUnit__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrTextSliceRValue textSlice
                    && textSlice.TextValue is MidLevelIrParameterOperand { Name: "text" }
                    && textSlice.Type.Kind == StarkTypeKind.Ascii
                    && textSlice.Start.Type.DisplayName == "i64"
                    && textSlice.Length is MidLevelIrIntegerConstantOperand { Value: var lengthValue, Type.DisplayName: "i64" }
                    && lengthValue == System.Numerics.BigInteger.One);
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
    public void ManifestBackedGenericBodiesPreferTypedChainedFieldIndexTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-field-index-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct SliceBox<T> {
                    i32[] Values;
                }

                public fn i32 ReadBoxSliceAt<T>(SliceBox<T> box, i32 index, T tag) {
                    return box.Values[index];
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadBoxSliceAt") with
            {
                BodyText = """
                    {
                        return tag;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("ReadBoxSliceAt<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("box.Values[index]", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 index, i32 tag) {
                        stack i32[3] values = { 4, 7, 9 };
                        stack i32[] view = values;
                        stack Facade.SliceBox<i32> box = new Facade.SliceBox<i32>() { Values = view };
                        return Facade.ReadBoxSliceAt(box, index, tag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadBoxSliceAt__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Values", Text: "box.Values" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrLoadIndirectRValue);
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
    public void ManifestBackedGenericBodiesPreferTypedVoidReturnTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-void-return-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record ResetBox(i32 Value) { }

                public fn void ResetValue(borrow mut ResetBox box) {
                    box.Value = 0;
                }

                public fn void GuardedReset<T>(bool shouldStop, borrow mut ResetBox box, T tag) {
                    if (shouldStop) {
                        return;
                    }
                    ResetValue(box);
                    return;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.GuardedReset") with
            {
                BodyText = """
                    {
                        return;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
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
            Assert.Contains("GuardedReset<T>(", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("ResetValue(box);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(bool shouldStop, borrow mut Facade.ResetBox box, i32 tag) {
                        Facade.GuardedReset(shouldStop, box, tag);
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_GuardedReset__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.ResetValue" });
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
    public void ManifestBackedGenericBodiesPreferTypedObjectCreationTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-object-creation-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box<T>(T Value) { }

                public fn Box<T> Wrap<T>(T value, Box<T> fallback) {
                    return new Box<T>(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return fallback;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn Facade.Box<i32> Run(i32 value) {
                        return Facade.Wrap(value, new Facade.Box<i32>(0));
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value" });
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
    public void ManifestBackedGenericBodiesPreferTypedEnumCallTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-enum-call-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return Option<T>.None;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Option<i32> result = Facade.Wrap(value);
                        switch (result) {
                            case Facade.Option<i32>.Some(var payload):
                                return payload;
                            case Facade.Option<i32>.None:
                                return 0;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Some_0" });
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
    public void ManifestBackedGenericBodiesPreferTypedEnumValueTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-enum-value-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Marker<T> {
                    Empty,
                    Missing,
                }

                public fn Marker<T> EmptyLike<T>(T value) {
                    return Marker<T>.Empty;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.EmptyLike") with
            {
                BodyText = """
                    {
                        return Marker<T>.Missing;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Marker<i32> result = Facade.EmptyLike(value);
                        switch (result) {
                            case Facade.Marker<i32>.Empty:
                                return 0;
                            case Facade.Marker<i32>.Missing:
                                return 1;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_EmptyLike__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            var tagWrite = Assert.Single(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.Equal(
                System.Numerics.BigInteger.Zero,
                Assert.IsType<MidLevelIrIntegerConstantOperand>(Assert.IsType<MidLevelIrInsertFieldRValue>(tagWrite).Value).Value);
            Assert.DoesNotContain(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Some_0" });
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
    public void ManifestBackedGenericBodiesPreferTypedLiteralTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-literal-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i8 One<T>(T value) {
                    return 1;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.One") with
            {
                BodyText = """
                    {
                        return 2;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i8 Run(i32 value) {
                        return Facade.One(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_One__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            var integerConstants = CollectMirIntegerConstants(specialized);
            Assert.Contains(System.Numerics.BigInteger.One, integerConstants);
            Assert.DoesNotContain(new System.Numerics.BigInteger(2), integerConstants);
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
    public void ManifestBackedGenericBodiesPreferTypedEnumConstructorTemplateBodiesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-typed-enum-constructor-body-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T> {
                    Value { Data: T, Tag: i32 },
                }

                public fn Boxed<T> Wrap<T>(T value) {
                    return Boxed<T>.Value { Data: value, Tag: 1 };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return Boxed<T>.Value { Data: value, Tag: 2 };
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Boxed<i32> result = Facade.Wrap(value);
                        switch (result) {
                            case Facade.Boxed<i32>.Value { Data: _, Tag: var tag }:
                                return tag;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            var integerConstants = CollectMirIntegerConstants(specialized);
            Assert.Contains(System.Numerics.BigInteger.One, integerConstants);
            Assert.DoesNotContain(new System.Numerics.BigInteger(2), integerConstants);
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
    public void ManifestBackedGenericBodiesUsePublishedConversionTargetsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-conversion-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 Truncate<T>(f32 value, T tag) {
                    return (i32)value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Truncate") with
            {
                BodyText = """
                    {
                        return (i64)value;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(f32 value) {
                        return Facade.Truncate(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Truncate__f32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrConvertRValue
                {
                    TargetType.Kind: StarkTypeKind.Integer,
                    TargetType.BitWidth: 32
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
    public void ManifestBackedGenericBodiesUsePublishedEnumConstructorFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-constructor-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T> {
                    Value { Data: T, Tag: i32 },
                }

                public fn Boxed<T> Wrap<T>(T value) {
                    return Boxed<T>.Value { Data: value, Tag: 1 };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return Boxed<T>.Missing { Wrong: value, AlsoWrong: 1 };
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Boxed<i32> boxed = Facade.Wrap(value);
                        switch (boxed) {
                            case Facade.Boxed<i32>.Value { Data: var data, Tag: var tag }:
                                return data + tag;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Value_Data" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Value_Tag" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumCallFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-call-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Wrap") with
            {
                BodyText = """
                    {
                        return Option<T>.Missing(value);
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Option<i32> result = Facade.Wrap(value);
                        switch (result) {
                            case Facade.Option<i32>.Some(var payload):
                                return payload;
                            case Facade.Option<i32>.None:
                                return 0;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Wrap__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Some_0" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumValueFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-value-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.EmptyLike") with
            {
                BodyText = """
                    {
                        return Option<T>.Missing;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Option<i32> result = Facade.EmptyLike(value);
                        switch (result) {
                            case Facade.Option<i32>.Some(var payload):
                                return payload;
                            case Facade.Option<i32>.None:
                                return 0;
                        }
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_EmptyLike__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$tag" });
            Assert.DoesNotContain(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "$Some_0" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumPatternFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-pattern-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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

                public fn i32 HasValue<T>(Option<T> value) {
                    switch (value) {
                        case Option<T>.Some(var payload):
                            return 1;
                        case Option<T>.None:
                            return 0;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.HasValue") with
            {
                BodyText = """
                    {
                        switch (value) {
                            case Option<T>.Missing(var payload):
                                return 1;
                            case Option<T>.Absent:
                                return 0;
                        }
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.HasValue(Facade.Option<i32>.Some(value));
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_HasValue__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "$tag" });
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
    public void ManifestBackedGenericBodiesUsePublishedEnumPatternMemberFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-enum-pattern-member-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public enum Boxed<T> {
                    Value { Data: T, Tag: i32 },
                }

                public fn i32 ReadTag<T>(Boxed<T> boxed) {
                    switch (boxed) {
                        case Boxed<T>.Value { Data: _, Tag: var tag }:
                            return tag;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadTag") with
            {
                BodyText = """
                    {
                        switch (boxed) {
                            case Boxed<T>.Value { Wrong: _, AlsoWrong: var tag }:
                                return tag;
                        }
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.ReadTag(Facade.Boxed<i32>.Value { Data: value, Tag: 7 });
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadTag__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "$Value_Tag" });
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
    public void ManifestBackedGenericBodiesUsePublishedAggregatePatternFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-aggregate-pattern-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32 Value, i32 Count) { }

                public fn i32 ReadCount<T>(Counter counter, T tag) {
                    switch (counter) {
                        case Counter(_, var count):
                            return count;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadCount") with
            {
                BodyText = """
                    {
                        switch (counter) {
                            case Missing(_, var count):
                                return count;
                        }
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.ReadCount(new Facade.Counter(value, 7), value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadCount__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Count" });
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
    public void ManifestBackedGenericBodiesUsePublishedLiteralAndGuardedSwitchFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-literal-guard-switch-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32 ClassifySwitch<T>(i32 value, T tag) {
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
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ClassifySwitch") with
            {
                BodyText = """
                    {
                        switch (value) {
                            case 99:
                                return 10;
                            case var current when current < 0:
                                return 0;
                            default:
                                return -100;
                        }
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.ClassifySwitch(value, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ClassifySwitch__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(specialized.Locals, static local => local is { Name: "current", StorageClass: "match" });

            var values = specialized.Blocks
                .SelectMany(static block => block.Statements)
                .Select(static statement => statement.Value)
                .ToArray();
            Assert.True(values.Count(static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.Equal }) >= 2);
            Assert.Contains(values, static value => value is MidLevelIrBinaryRValue { Operator: MidLevelIrBinaryOperator.GreaterThan });
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
    public void ManifestBackedGenericBodiesUsePublishedObjectInitializerMembersWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-object-initializer-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Pair<T> {
                    T Value;
                    i32 Count;
                }

                public fn Pair<T> MakePair<T>(T value) {
                    return new Pair<T>() { Value = value, Count = 1 };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.MakePair") with
            {
                BodyText = """
                    {
                        return new Pair<T>() { Missing = value, Wrong = 1 };
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Pair<i32> pair = Facade.MakePair(value);
                        return pair.Count;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_MakePair__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value", FieldIndex: 0 });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Count", FieldIndex: 1 });
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
    public void ManifestBackedGenericBodiesUsePublishedObjectCreationTypesWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-object-type-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Pair<T> {
                    T Value;
                    i32 Count;
                }

                public fn Pair<T> MakePair<T>(T value) {
                    return new Pair<T>() { Value = value, Count = 1 };
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.MakePair") with
            {
                BodyText = """
                    {
                        return new Missing<T>() { Value = value, Count = 1 };
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Pair<i32> pair = Facade.MakePair(value);
                        return pair.Count;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_MakePair__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value", FieldIndex: 0 });
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Count", FieldIndex: 1 });
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
    public void ManifestBackedGenericBodiesUsePublishedDirectCallTargetsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-direct-call-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward") with
            {
                BodyText = """
                    {
                        return Missing(value);
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Forward(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            Assert.Contains(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Identity__i32");

            var forward = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.True(forward.SupportsDirectCodeGeneration);
            Assert.Contains(
                forward.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "__stark_mono_fn_Demo__Facade_Identity__i32" });
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
    public void ManifestBackedGenericBodiesUsePublishedFieldAccessFactsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-field-access-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.ReadValue") with
            {
                BodyText = """
                    {
                        return pair.Missing;
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Pair<i32> pair) {
                        return Facade.ReadValue(pair);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_ReadValue__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrExtractFieldRValue { FieldName: "Value", FieldIndex: 0 });
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
    public void ManifestBackedGenericBodiesUsePublishedMemberCallTargetsWhenBridgeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-member-call-facts-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Box(i32 Dummy) {
                    fn i32 Echo(borrow Box self, i32 value) {
                        return value;
                    }
                }

                public fn i32 Forward<T>(T tag, Box box, i32 value) {
                    return box.Echo(value);
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            var corruptedTemplate = Assert.Single(facadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward") with
            {
                BodyText = """
                    {
                        return box.Missing(value);
                    }
                    """
            };

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
                                module.EffectiveGenericTemplates!.Functions
                                    .Select(template => template.QualifiedResolvedName == corruptedTemplate.QualifiedResolvedName
                                        ? corruptedTemplate
                                        : template)
                                    .ToArray())
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Box box, i32 value) {
                        return Facade.Forward(value, box, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrCallRValue { FunctionName: "Facade.Box.Echo" });
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
    public void ManifestBackedGenericBodiesPreserveTransitiveImportedModuleSurfaceAcrossPackageImages()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-transitive-import-generic-body-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            File.WriteAllText(
                mathPath,
                """
                module Math

                public fn T Identity<T>(T value) {
                    return value;
                }
                """);

            var libraryResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Facade

                    public fn T Forward<T>(T value) {
                        return Math.Identity(value);
                    }
                    """,
                    facadePath),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var typedOnlyManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module with
                    {
                        Functions = [],
                        Types = [],
                        Globals = [],
                        TypeAliases = [],
                        TypedInterface = module.EffectiveTypedInterface,
                        CompilerFacts = module.EffectiveCompilerFacts,
                        GenericTemplates = module.EffectiveGenericTemplates
                    })
                    .ToArray()
            };
            var typedFacadeModule = Assert.Single(typedOnlyManifest.Modules, static module => module.ModuleName == "Facade");
            var forwardTemplate = Assert.Single(typedFacadeModule.GenericTemplates!.Functions, static template => template.QualifiedResolvedName == "Facade.Forward");
            Assert.NotNull(forwardTemplate.TypedBody);
            var forwardReturn = Assert.Single(forwardTemplate.TypedBody!.Statements);
            Assert.Equal("return", forwardReturn.Kind);
            Assert.Equal("direct-call", forwardReturn.Expression.Kind);
            var forwardDirectCall = Assert.Single(forwardTemplate.DirectCalls!);
            Assert.Equal("Math.Identity", forwardDirectCall.QualifiedResolvedName);
            Assert.Equal("Math.Identity", forwardDirectCall.QualifiedSourceName);
            Assert.Equal("Math.Identity", forwardDirectCall.QualifiedTemplateName);

            Assert.True(
                PackageImageLoader.TryBuildModuleSource(
                    new ResolvedPackageModule(
                        manifestPath,
                        Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                        typedOnlyManifest,
                        typedFacadeModule),
                    out var sourceText));
            Assert.Contains("import Math", sourceText, StringComparison.Ordinal);
            Assert.Contains("public fn T Forward<T>(T value)", sourceText, StringComparison.Ordinal);
            Assert.DoesNotContain("return Math.Identity(value);", sourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);
            File.Delete(mathPath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Forward(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedFacade));
            Assert.NotNull(importedFacade);
            Assert.True(loadedModules.TryGet("Math", out var importedMath));
            Assert.NotNull(importedMath);
            Assert.Contains(importedFacade.SyntaxModel.Imports, static import => import.ModuleName == "Math" && !import.IsReExport);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            Assert.Contains(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Math_Identity__i32");
            var forward = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.Contains(
                forward.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value).OfType<MidLevelIrCallRValue>(),
                static call => call.FunctionName == "__stark_mono_fn_Demo__Math_Identity__i32");
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
    public void NestedGenericCallsMaterializeTransitiveSpecializationsIntoHighLevelIr()
    {
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn T Forward<T>(T value) {
                    return Identity(value);
                }

                fn i32 Run(i32 value) {
                    return Forward(value);
                }
                """,
                "/virtual/Demo.stark"),
            new CompilerOptions(StopAfterPassId: "lower-hir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.NotNull(hir);

        Assert.Contains(hir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Forward__i32");
        Assert.Contains(hir.Functions, static function => function.Name == "__stark_mono_fn_Demo__Identity__i32");
    }

    [Fact]
    public void ManifestBackedModulesPreservePublishedAbiFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-abi-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Big {
                    i64 A;
                    i64 B;
                    i64 C;
                }

                public fn Big Make();
                public fn i64 Read(Big value);
                public fn i32 Add(i32 value);
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.TypedInterface);
            Assert.NotNull(facadeModule.CompilerFacts);

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
                            CompilerFacts = facadeModule.CompilerFacts
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run() {
                        return 0;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
            Assert.NotNull(abiModel);

            var add = abiModel.Functions["Facade.Add"];
            Assert.Equal("Facade.Add", add.SymbolName);
            Assert.False(add.ReturnsIndirect);
            Assert.Equal(AbiParameterKind.Direct, Assert.Single(add.UserParameters).Kind);

            var make = abiModel.Functions["Facade.Make"];
            Assert.Equal("Facade.Make", make.SymbolName);
            Assert.True(make.ReturnsIndirect);
            Assert.Equal(AbiParameterKind.SRet, Assert.Single(make.Parameters).Kind);

            var read = abiModel.Functions["Facade.Read"];
            Assert.Equal("Facade.Read", read.SymbolName);
            Assert.Equal(AbiParameterKind.IndirectIn, Assert.Single(read.UserParameters).Kind);
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
    public void ManifestBackedMethodsPreservePublishedAbiFactsFromCompilerFactSections()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-image-import-method-abi-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Counter {
                    i32 Value;

                    fn void Reset(borrow mut Counter self) {
                        self.Value = 0;
                        return;
                    }
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static d => d.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));

            File.WriteAllText(manifestPath, manifest.ToJson());
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
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "lower-abi"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.AbiModel, out AbiModel? abiModel));
            Assert.NotNull(abiModel);

            var reset = abiModel.Functions["Facade.Counter.Reset"];
            Assert.Equal("Facade.Counter.Reset", reset.SymbolName);
            Assert.Equal(AbiParameterKind.IndirectIn, Assert.Single(reset.UserParameters).Kind);
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
    public void PackageImageDocumentResolversLoadStructuredImportsWithoutAnySourceText()
    {
        var bitsModule = new StarkPackageModuleManifest(
            "Bits",
            ReExports: [],
            Functions: [],
            Types: [],
            Globals: [],
            TypeAliases: [],
            TypedInterface: new StarkPackageTypedInterfaceSection(
                Functions: [],
                Types:
                [
                    new StarkPackageTypedTypeManifest(
                        Name: "Token",
                        QualifiedName: "Bits.Token",
                        Visibility: "public",
                        Kind: "record",
                        Fields:
                        [
                            new StarkPackageTypedFieldManifest(
                                "Value",
                                new StarkPackageTypeReference("integer", BitWidth: 32))
                        ])
                ],
                Globals: [],
                TypeAliases: []));
        var facadeModule = new StarkPackageModuleManifest(
            "Facade",
            ReExports:
            [
                new StarkPackageReExportManifest("Bits")
            ],
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
                TypeAliases: []));

        Assert.True(
            PackageImageLoader.TryBuildModuleDocument(
                new ResolvedPackageModule(
                    "/virtual/Bits.starkpkg.json",
                    "/virtual/libBits.a",
                    new StarkPackageManifest("Bits", "libBits.a", [bitsModule]),
                    bitsModule),
                out var bitsDocument));
        Assert.True(
            PackageImageLoader.TryBuildModuleDocument(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule]),
                    facadeModule),
                out var facadeDocument));

        var resolver = new DocumentOnlyModuleResolver(bitsDocument, facadeDocument);
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                fn void Run() {
                    return;
                }
                """),
            new CompilerOptions(
                ModuleResolver: resolver,
                StopAfterPassId: "load-modules"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));
        Assert.Equal(0, resolver.SourceLoadAttempts);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.HasModule("Facade"));
        Assert.True(moduleGraph.HasModule("Bits"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
        Assert.NotNull(loadedModules);
        Assert.True(loadedModules.TryGet("Facade", out var importedFacade));
        Assert.NotNull(importedFacade);
        Assert.True(loadedModules.TryGet("Bits", out var importedBits));
        Assert.NotNull(importedBits);
        Assert.Contains(importedFacade.SyntaxModel.Imports, static import => import.ModuleName == "Bits" && import.IsReExport);
        Assert.Contains(importedBits.SyntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Record && declaration.Name == "Token");
        Assert.Contains(importedFacade.SyntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Identity");
    }

    [Fact]
    public void PackageImageDocumentResolversLoadNonReExportImportsWithoutAnySourceText()
    {
        var mathModule = new StarkPackageModuleManifest(
            "Math",
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
                        QualifiedName: "Math.Identity",
                        Visibility: "public",
                        SymbolName: "Math.Identity",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference("integer", BitWidth: 32),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "value",
                                new StarkPackageTypeReference("integer", BitWidth: 32))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases: []));
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
                        Name: "Forward",
                        QualifiedName: "Facade.Forward",
                        Visibility: "public",
                        SymbolName: "Facade.Forward",
                        Kind: "fn",
                        ReturnType: new StarkPackageTypeReference("integer", BitWidth: 32),
                        Parameters:
                        [
                            new StarkPackageTypedParameterManifest(
                                "value",
                                new StarkPackageTypeReference("integer", BitWidth: 32))
                        ],
                        IsFfi: false,
                        IsStrictFp: false,
                        UseFastCallingConvention: true)
                ],
                Types: [],
                Globals: [],
                TypeAliases: []),
            Imports:
            [
                new StarkPackageImportManifest("Math", IsExported: false)
            ]);

        Assert.True(
            PackageImageLoader.TryBuildModuleDocument(
                new ResolvedPackageModule(
                    "/virtual/Math.starkpkg.json",
                    "/virtual/libMath.a",
                    new StarkPackageManifest("Math", "libMath.a", [mathModule]),
                    mathModule),
                out var mathDocument));
        Assert.True(
            PackageImageLoader.TryBuildModuleDocument(
                new ResolvedPackageModule(
                    "/virtual/Facade.starkpkg.json",
                    "/virtual/libFacade.a",
                    new StarkPackageManifest("Facade", "libFacade.a", [facadeModule, mathModule]),
                    facadeModule),
                out var facadeDocument));

        var resolver = new DocumentOnlyModuleResolver(mathDocument, facadeDocument);
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                import Facade
                module Demo

                fn void Run() {
                    return;
                }
                """),
            new CompilerOptions(
                ModuleResolver: resolver,
                StopAfterPassId: "load-modules"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.Equal(0, resolver.SourceLoadAttempts);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
        Assert.NotNull(moduleGraph);
        Assert.True(moduleGraph.HasModule("Facade"));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
        Assert.NotNull(loadedModules);
        Assert.True(loadedModules.TryGet("Facade", out var importedFacade));
        Assert.NotNull(importedFacade);
        Assert.True(loadedModules.TryGet("Math", out var importedMath));
        Assert.NotNull(importedMath);
        Assert.Contains(importedFacade.SyntaxModel.Imports, static import => import.ModuleName == "Math" && !import.IsReExport);
        Assert.Contains(importedFacade.SyntaxModel.Declarations, static declaration => declaration.Kind == DeclarationKind.Function && declaration.Name == "Forward");
    }

    [Fact]
    public void ManifestBackedGenericFunctionsRecordInstantiationTriggersWithoutSourceFiles()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-function-trigger-pipeline-");
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
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run() {
                        stack i32 value = 4;
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static d => d.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);

            var trigger = Assert.Single(typeCheckModel.InstantiationTriggers);
            Assert.Equal("Facade.Identity", trigger.FunctionName);
            Assert.Equal(["i32"], trigger.TypeArguments.Select(static type => type.DisplayName));
            Assert.Equal("i32", trigger.Signature.ReturnType.DisplayName);
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
    public void RootGenericInstantiationsStayOwnedByTheRootModule()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Pair<T>(T Value) { }

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32 Run(Pair<i32> pair) {
                    return Identity(pair.Value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "instantiation-ownership"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.InstantiationOwnership, out InstantiationOwnershipModel? ownership));
        Assert.NotNull(ownership);

        var functionOwnership = Assert.Single(ownership.Functions);
        Assert.Equal("Identity", functionOwnership.TemplateName);
        Assert.Equal("Demo", functionOwnership.DeclaringModuleName);
        Assert.Equal("Demo", functionOwnership.OwnerModuleName);
        Assert.True(functionOwnership.IsDeclaringModuleSourceBacked);
        Assert.False(functionOwnership.RequiresConsumerOwnership);

        var typeOwnership = Assert.Single(ownership.Types);
        Assert.Equal("Pair", typeOwnership.TemplateName);
        Assert.Equal("Pair<i32>", typeOwnership.InstantiatedTypeName);
        Assert.Equal("Demo", typeOwnership.DeclaringModuleName);
        Assert.Equal("Demo", typeOwnership.OwnerModuleName);
        Assert.True(typeOwnership.IsDeclaringModuleSourceBacked);
        Assert.False(typeOwnership.RequiresConsumerOwnership);
    }

    [Fact]
    public void SourceBackedImportedGenericInstantiationsStayOwnedByTheDefiningModule()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-generic-ownership-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Facade.stark"),
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T Identity<T>(T value) {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Pair<i32> pair) {
                        return Facade.Identity(pair.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "instantiation-ownership",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.InstantiationOwnership, out InstantiationOwnershipModel? ownership));
            Assert.NotNull(ownership);

            var functionOwnership = Assert.Single(ownership.Functions);
            Assert.Equal("Facade.Identity", functionOwnership.TemplateName);
            Assert.Equal("Facade", functionOwnership.DeclaringModuleName);
            Assert.Equal("Facade", functionOwnership.OwnerModuleName);
            Assert.True(functionOwnership.IsDeclaringModuleSourceBacked);
            Assert.False(functionOwnership.RequiresConsumerOwnership);

            var typeOwnership = Assert.Single(ownership.Types);
            Assert.Equal("Facade.Pair", typeOwnership.TemplateName);
            Assert.Equal("Facade.Pair<i32>", typeOwnership.InstantiatedTypeName);
            Assert.Equal("Facade", typeOwnership.DeclaringModuleName);
            Assert.Equal("Facade", typeOwnership.OwnerModuleName);
            Assert.True(typeOwnership.IsDeclaringModuleSourceBacked);
            Assert.False(typeOwnership.RequiresConsumerOwnership);
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
    public void ManifestBackedGenericInstantiationsFallBackToTheRootConsumerModule()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-ownership-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T Identity<T>(T value);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Pair<i32> pair) {
                        return Facade.Identity(pair.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "instantiation-ownership",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.InstantiationOwnership, out InstantiationOwnershipModel? ownership));
            Assert.NotNull(ownership);

            var functionOwnership = Assert.Single(ownership.Functions);
            Assert.Equal("Facade.Identity", functionOwnership.TemplateName);
            Assert.Equal("Facade", functionOwnership.DeclaringModuleName);
            Assert.Equal("Demo", functionOwnership.OwnerModuleName);
            Assert.False(functionOwnership.IsDeclaringModuleSourceBacked);
            Assert.True(functionOwnership.RequiresConsumerOwnership);

            var typeOwnership = Assert.Single(ownership.Types);
            Assert.Equal("Facade.Pair", typeOwnership.TemplateName);
            Assert.Equal("Facade.Pair<i32>", typeOwnership.InstantiatedTypeName);
            Assert.Equal("Facade", typeOwnership.DeclaringModuleName);
            Assert.Equal("Demo", typeOwnership.OwnerModuleName);
            Assert.False(typeOwnership.IsDeclaringModuleSourceBacked);
            Assert.True(typeOwnership.RequiresConsumerOwnership);
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
    public void RootGenericInstantiationsGetFullySpelledMonomorphizationSymbols()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Pair<T>(T Value) { }

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32 Run(Pair<i32> pair) {
                    return Identity(pair.Value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var function = Assert.Single(plan.Functions);
        Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
        Assert.Equal(1, function.EstimatedTopLevelStatementCount);
        Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", function.SymbolName);

        var type = Assert.Single(plan.Types);
        Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, type.Linkage);
        Assert.Equal("__stark_mono_ty_Demo__Pair__i32", type.SymbolName);
    }

    [Fact]
    public void SourceBackedImportedGenericInstantiationsUseDefiningModuleMonomorphizationSymbols()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-generic-mono-plan-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Facade.stark"),
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T Identity<T>(T value) {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Pair<i32> pair) {
                        return Facade.Identity(pair.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.InlineSmallBody, function.CodeSizeHeuristic);
            Assert.Equal(1, function.EstimatedTopLevelStatementCount);
            Assert.Equal(MonomorphizationLinkageKind.LinkOnceOdrComdat, function.Linkage);
            Assert.Equal("__stark_mono_fn_Facade__Facade_Identity__i32", function.SymbolName);

            var type = Assert.Single(plan.Types);
            Assert.Equal(MonomorphizationLinkageKind.LinkOnceOdrComdat, type.Linkage);
            Assert.Equal("__stark_mono_ty_Facade__Facade_Pair__i32", type.SymbolName);
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
    public void ManifestBackedGenericInstantiationsUseRootOwnedMonomorphizationSymbols()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<T>(T Value) { }

                public fn T Identity<T>(T value);
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(Facade.Pair<i32> pair) {
                        return Facade.Identity(pair.Value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.DeclarationOnly, function.CodeSizeHeuristic);
            Assert.Null(function.EstimatedTopLevelStatementCount);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Identity__i32", function.SymbolName);

            var type = Assert.Single(plan.Types);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, type.Linkage);
            Assert.Equal("__stark_mono_ty_Demo__Facade_Pair__i32", type.SymbolName);
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
    public void ManifestBackedColdGenericInstantiationsPreservePackageImagePlanningFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-cold-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public cold fn T Choose<T>(T left, T right, bool takeRight) {
                    stack T current = left;
                    if (takeRight) {
                        current = right;
                    }

                    return current;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 left, i32 right, bool takeRight) {
                        return Facade.Choose(left, right, takeRight);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.FunctionTemplates.TryGetValue("Facade.Choose", out var templateSummary));
            Assert.Equal(3, templateSummary.TopLevelStatementCount);

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.ReduceCodeSize, function.CodeSizeHeuristic);
            Assert.Equal(3, function.EstimatedTopLevelStatementCount);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Choose__i32", function.SymbolName);
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
    public void ManifestBackedLargeAggregateGenericInstantiationsPreferCodeSizeReductionFromPublishedLayoutFacts()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-layout-aware-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Big(i64 A, i64 B, i64 C) { }

                public fn T Bounce<T>(T value) {
                    return value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

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

                    fn Facade.Big Run(Facade.Big value) {
                        return Facade.Bounce(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(loadedModules.TryGet("Facade", out var importedModule));
            Assert.NotNull(importedModule);
            Assert.NotNull(importedModule.PackageImageFacts);
            Assert.True(importedModule.PackageImageFacts!.ConcreteLayouts.ContainsKey("Facade.Big"));

            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(MonomorphizationCodeSizeHeuristic.ReduceCodeSize, function.CodeSizeHeuristic);
            Assert.Equal(1, function.EstimatedTopLevelStatementCount);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Bounce__Facade_Big", function.SymbolName);
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
    public void RepeatedManifestBackedNestedGenericInstantiationsStayRootOwnedAndDeduplicatedInMonomorphizationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-nested-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

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
                            TypedInterface = facadeModule.TypedInterface,
                            CompilerFacts = facadeModule.CompilerFacts,
                            GenericTemplates = facadeModule.GenericTemplates
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 left, i32 right) {
                        return Facade.Forward(left) + Facade.Forward(right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            Assert.Equal(2, plan.Functions.Count);

            var forward = Assert.Single(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.Equal("Facade.Forward", forward.TemplateName);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, forward.Linkage);

            var identity = Assert.Single(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Identity__i32");
            Assert.Equal("Facade.Identity", identity.TemplateName);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, identity.Linkage);
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
    public void ManifestBackedNestedGenericPlanningUsesPublishedDeferredTriggers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-published-deferred-generic-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

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
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.GenericTemplates);

            var rewrittenTemplates = facadeModule.GenericTemplates!.Functions
                .Select(template => template.QualifiedResolvedName == "Facade.Forward"
                    ? template with { BodyText = "{\n    return value;\n}" }
                    : template)
                .ToArray();

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
                            GenericTemplates = new StarkPackageGenericTemplateSection(rewrittenTemplates)
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 left, i32 right) {
                        return Facade.Forward(left) + Facade.Forward(right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);
            Assert.Equal(2, plan.Functions.Count);

            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Forward__i32");
            Assert.Contains(plan.Functions, static function => function.SymbolName == "__stark_mono_fn_Demo__Facade_Identity__i32");
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
    public void ManifestBackedNestedGenericTypePlanningUsesPublishedDeferredTypeTriggers()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-published-deferred-generic-type-mono-plan-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Pair<A, B>(A First, B Second) { }

                public fn i32 Forward<T>(T value, bool flag) {
                    stack Pair<T, bool> pair = new Pair<T, bool>(value, flag);
                    return pair.Second ? 1 : 0;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = WithEffectiveLegacyCompilerSectionCopies(Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade"));
            Assert.NotNull(facadeModule.GenericTemplates);

            var rewrittenTemplates = facadeModule.GenericTemplates!.Functions
                .Select(template => template.QualifiedResolvedName == "Facade.Forward"
                    ? template with { BodyText = "{\n    return 0;\n}" }
                    : template)
                .ToArray();

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
                            GenericTemplates = new StarkPackageGenericTemplateSection(rewrittenTemplates)
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value, bool flag) {
                        return Facade.Forward(value, flag);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "monomorphization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Forward__i32", function.SymbolName);

            var type = Assert.Single(plan.Types);
            Assert.Equal("Facade.Pair", type.TemplateName);
            Assert.Equal("Facade.Pair<i32,bool>", type.InstantiatedTypeName);
            Assert.Equal("__stark_mono_ty_Demo__Facade_Pair__i32__bool", type.SymbolName);
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
    public void ColdGenericInstantiationsPreferCodeSizeReductionInThePlan()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                cold fn T Choose<T>(T left, T right, bool takeRight) {
                    stack T current = left;
                    if (takeRight) {
                        current = right;
                    }

                    return current;
                }

                fn i32 Run(i32 left, i32 right, bool takeRight) {
                    return Choose(left, right, takeRight);
                }
                """),
            new CompilerOptions(StopAfterPassId: "monomorphization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MonomorphizationPlan, out MonomorphizationPlanModel? plan));
        Assert.NotNull(plan);

        var function = Assert.Single(plan.Functions);
        Assert.Equal(MonomorphizationCodeSizeHeuristic.ReduceCodeSize, function.CodeSizeHeuristic);
        Assert.Equal(3, function.EstimatedTopLevelStatementCount);
        Assert.Equal("__stark_mono_fn_Demo__Choose__i32", function.SymbolName);
    }

    [Fact]
    public void RootGenericInstantiationsPreferOwnedConcreteBodiesInSpecializationPlan()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32 Run(i32 value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialization-plan"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
        Assert.NotNull(plan);

        var function = Assert.Single(plan.Functions);
        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", function.SymbolName);
        Assert.Equal(
            new[] { FunctionSpecializationStrategy.OwnedConcreteBody },
            function.SelectionOrder);
        Assert.Equal(FunctionSpecializationCodeGenerationMode.SingleOwnerConcreteBody, function.CodeGenerationMode);
    }

    [Fact]
    public void SourceBackedImportedLawGenericsPreferCallerCloneBeforeOwnedBodyInSpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-generic-specialization-plan-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers {
                    finite law T Identity<T>(T value) {
                        return value;
                    }
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32 Run(i32 value) {
                        return Math.Numbers.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal("__stark_mono_fn_Math__Math_Numbers_Identity__i32", function.SymbolName);
            Assert.Equal(
                new[]
                {
                    FunctionSpecializationStrategy.LawCallerSpecializedClone,
                    FunctionSpecializationStrategy.OwnedConcreteBody,
                    FunctionSpecializationStrategy.DirectAbiBoundaryFallback
                },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.CallerSpecializedClone, function.CodeGenerationMode);
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
    public void ColdImportedLawGenericsPreferOwnedBodyOverCallerCloneInSpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cold-source-generic-specialization-plan-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers {
                    cold finite law T Identity<T>(T value) {
                        return value;
                    }
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32 Run(i32 value) {
                        return Math.Numbers.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(
                new[]
                {
                    FunctionSpecializationStrategy.OwnedConcreteBody,
                    FunctionSpecializationStrategy.DirectAbiBoundaryFallback
                },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.SingleOwnerConcreteBody, function.CodeGenerationMode);
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
    public void DeclarationOnlyImportedGenericInstantiationsFallBackToAbiOnlySpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-specialization-plan-pipeline-");
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

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(
                new[] { FunctionSpecializationStrategy.DirectAbiBoundaryFallback },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.AbiBoundaryOnly, function.CodeGenerationMode);
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
    public void ManifestBackedImportedGenericsWithoutPublishedAbiFactsPreferOwnedBodyOnlyInSpecializationPlan()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-specialization-abi-facts-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.NotNull(facadeModule.CompilerSections?.CompilerFacts?.AbiFunctions);

            var abiStrippedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            CompilerSections = module.CompilerSections! with
                            {
                                CompilerFacts = module.CompilerSections.CompilerFacts! with
                                {
                                    AbiFunctions = []
                                }
                            }
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, abiStrippedManifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationPlan, out SpecializationPlanModel? plan));
            Assert.NotNull(plan);

            var function = Assert.Single(plan.Functions);
            Assert.Equal(
                new[] { FunctionSpecializationStrategy.OwnedConcreteBody },
                function.SelectionOrder);
            Assert.Equal(FunctionSpecializationCodeGenerationMode.SingleOwnerConcreteBody, function.CodeGenerationMode);
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
    public void ConflictingSpecializationSymbolPlansReportAmbiguityDiagnostics()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-specialization-plan-ambiguity-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers {
                    finite law T Identity<T>(T value) {
                        return value;
                    }
                }

                public finite law T Numbers_Identity<T>(T value) {
                    return value;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32 Run(i32 value) {
                        stack i32 left = Math.Numbers_Identity(value);
                        return Math.Numbers.Identity(left);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-plan",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.False(result.Succeeded);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "STK4115"
                    && diagnostic.Stage == "specialization-plan"
                    && diagnostic.Message.Contains("__stark_mono_fn_Math__Math_Numbers_Identity__i32", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("Math.Numbers.Identity<i32>", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("Math.Numbers_Identity<i32>", StringComparison.Ordinal)
                    && diagnostic.Message.Contains("different specialization priority orders", StringComparison.Ordinal));
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "STK4116"
                    && diagnostic.Stage == "specialization-plan"
                    && diagnostic.Message.Contains("Math.Numbers.Identity<i32>", StringComparison.Ordinal));
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
    public void RootGenericInstantiationsChooseOwnedBodyCodegenStrategy()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32 Run(i32 value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialization-codegen-strategy"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
        Assert.NotNull(strategy);

        var function = Assert.Single(strategy.Functions);
        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", function.SymbolName);
        Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
        Assert.Equal(FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBody, function.StrategyKind);
        Assert.False(function.SupportsAbiFallback);
    }

    [Fact]
    public void SourceBackedImportedLawGenericsChooseLawCloneAwareCodegenStrategy()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-generic-codegen-strategy-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers {
                    finite law T Identity<T>(T value) {
                        return value;
                    }
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32 Run(i32 value) {
                        return Math.Numbers.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-codegen-strategy",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
            Assert.NotNull(strategy);

            var function = Assert.Single(strategy.Functions);
            Assert.Equal("__stark_mono_fn_Math__Math_Numbers_Identity__i32", function.SymbolName);
            Assert.Equal(MonomorphizationLinkageKind.LinkOnceOdrComdat, function.Linkage);
            Assert.Equal(FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBodyAndPreferLawCallerClone, function.StrategyKind);
            Assert.True(function.SupportsAbiFallback);
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
    public void ColdImportedLawGenericsAvoidCloneInCodegenStrategy()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-cold-source-generic-codegen-strategy-pipeline-");

        try
        {
            File.WriteAllText(
                Path.Combine(tempDirectory.FullName, "Math.stark"),
                """
                module Math

                public doctrine Numbers {
                    cold finite law T Identity<T>(T value) {
                        return value;
                    }
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    finite law i32 Run(i32 value) {
                        return Math.Numbers.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-codegen-strategy",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
            Assert.NotNull(strategy);

            var function = Assert.Single(strategy.Functions);
            Assert.Equal(FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBody, function.StrategyKind);
            Assert.True(function.SupportsAbiFallback);
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
    public void DeclarationOnlyImportedGenericInstantiationsChooseAbiFallbackOnlyCodegenStrategy()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-codegen-strategy-pipeline-");
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

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            File.WriteAllText(manifestPath, manifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-codegen-strategy",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
            Assert.NotNull(strategy);

            var function = Assert.Single(strategy.Functions);
            Assert.Equal("__stark_mono_fn_Demo__Facade_Identity__i32", function.SymbolName);
            Assert.Equal(MonomorphizationLinkageKind.InternalSingleOwner, function.Linkage);
            Assert.Equal(FunctionSpecializationCodegenStrategyKind.AbiFallbackOnly, function.StrategyKind);
            Assert.True(function.SupportsAbiFallback);
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
    public void ManifestBackedImportedGenericsWithoutPublishedAbiFactsDoNotClaimAbiFallbackInCodegenStrategy()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-generic-codegen-abi-facts-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn T Identity<T>(T value) {
                    return value;
                }
                """,
                Path.Combine(tempDirectory.FullName, "Facade.stark")));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

            var manifest = PackageImageBuilder.Create(
                libraryResult,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"));
            var facadeModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Facade");
            Assert.NotNull(facadeModule.CompilerSections?.CompilerFacts?.AbiFunctions);

            var abiStrippedManifest = manifest with
            {
                Modules = manifest.Modules
                    .Select(module => module.ModuleName == "Facade"
                        ? module with
                        {
                            CompilerSections = module.CompilerSections! with
                            {
                                CompilerFacts = module.CompilerSections.CompilerFacts! with
                                {
                                    AbiFunctions = []
                                }
                            }
                        }
                        : module)
                    .ToArray()
            };

            File.WriteAllText(manifestPath, abiStrippedManifest.ToJson());

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        return Facade.Identity(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "specialization-codegen-strategy",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.SpecializationCodegenStrategy, out SpecializationCodegenStrategyModel? strategy));
            Assert.NotNull(strategy);

            var function = Assert.Single(strategy.Functions);
            Assert.Equal(FunctionSpecializationCodegenStrategyKind.EmitOwnedConcreteBody, function.StrategyKind);
            Assert.False(function.SupportsAbiFallback);
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
    public void LowerHirMaterializesSourceBackedMonomorphizedGenericFunctions()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    stack T copy = value;
                    return copy;
                }

                fn i32 Run(i32 value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "lower-hir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.NotNull(hir);

        var monomorphized = Assert.Single(
            hir.Functions,
            static function => function.Name == "__stark_mono_fn_Demo__Identity__i32");
        Assert.True(monomorphized.HasBody);
        Assert.Equal(FunctionBodyLoweringKind.StarkCfg, monomorphized.BodyLoweringKind);
        Assert.Equal("Identity", monomorphized.Signature.TemplateName);
        Assert.Equal("Identity", monomorphized.BodyTemplateName);
        Assert.Equal("i32", monomorphized.Signature.ReturnType.DisplayName);
        Assert.Equal("i32", Assert.Single(monomorphized.Signature.Parameters).Type.DisplayName);
        Assert.NotNull(monomorphized.GenericTypeSubstitution);
        Assert.True(monomorphized.GenericTypeSubstitution!.TryGetValue("T", out var substitutedType));
        Assert.Equal("i32", substitutedType.DisplayName);
    }

    [Fact]
    public void LowerMirSubstitutesConcreteTypesInsideMaterializedGenericBodies()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    stack T copy = value;
                    return copy;
                }

                fn i32 Run(i32 value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);

        var monomorphized = Assert.Single(
            mir.Functions,
            static function => function.Name == "__stark_mono_fn_Demo__Identity__i32");
        Assert.True(monomorphized.HasBody);
        Assert.True(monomorphized.SupportsDirectCodeGeneration);
        Assert.Equal("i32", monomorphized.ReturnType.DisplayName);
        Assert.Equal("i32", Assert.Single(monomorphized.Parameters).Type.DisplayName);
        Assert.Contains(
            monomorphized.Locals,
            static local => local.Name == "copy" && local.Type.DisplayName == "i32");
    }

    [Fact]
    public void LowerMirUsesPublishedTemplateObjectCreationFactsForImportedGenericBodies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-imported-template-object-creations-lower-mir-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libDemo.starkpkg.json");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Demo.lib" : "libDemo.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var moduleSource =
                """
                module Demo

                public record Counter(i32 Value) { }

                public fn i32 MakeFlag<T>(T value) {
                    stack Counter counter = new Counter(0);
                    return 1;
                }

                fn i32 Run(i32 value) {
                    return MakeFlag(value);
                }
                """;
            var manifestResult = pipeline.Run(new CompilationInput(moduleSource, Path.Combine(tempDirectory.FullName, "Demo.stark")));
            Assert.True(manifestResult.Succeeded, string.Join(", ", manifestResult.Diagnostics.Select(static d => d.ToString())));
            var manifest = PackageImageBuilder.Create(manifestResult, libraryPath);
            var demoModule = Assert.Single(manifest.Modules, static module => module.ModuleName == "Demo");

            Assert.True(
                PackageImageLoader.TryBuildLoadedPackageImageFacts(
                    new ResolvedPackageModule(manifestPath, libraryPath, manifest, demoModule),
                    out var packageImageFacts));

            var result = pipeline.Run(
                new CompilationInput(moduleSource, Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(StopAfterPassId: "lower-hir"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static d => d.ToString())));

            var remappedFunctionTemplates = packageImageFacts.FunctionTemplates.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            if (packageImageFacts.FunctionTemplates.TryGetValue("Demo.MakeFlag", out var makeFlagTemplate))
            {
                remappedFunctionTemplates["MakeFlag"] = makeFlagTemplate with
                {
                    ObjectCreationSummaries = makeFlagTemplate.ObjectCreations
                        .Select(static objectCreation => objectCreation with
                        {
                            CreatedType = string.Equals(objectCreation.CreatedType.NamedType, "Demo.Counter", StringComparison.Ordinal)
                                ? StarkTypeSymbols.Named("Counter")
                                : objectCreation.CreatedType
                        })
                        .ToArray()
                };
            }

            var remappedPackageImageFacts = packageImageFacts with { FunctionTemplates = remappedFunctionTemplates };
            Assert.True(remappedPackageImageFacts.FunctionTemplates.TryGetValue("MakeFlag", out var importedTemplate));
            Assert.Single(importedTemplate.Constructors);

            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
            Assert.NotNull(hir);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LoadedModules, out LoadedModuleSet? loadedModules));
            Assert.NotNull(loadedModules);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
            Assert.NotNull(moduleGraph);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
            Assert.NotNull(typeModel);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.EnumLayoutModel, out EnumLayoutModel? enumLayoutModel));
            Assert.NotNull(enumLayoutModel);

            var prunedTypeModel = typeModel with
            {
                ObjectCreations = typeModel.ObjectCreations
                    .Where(static record => !string.Equals(record.EnclosingFunctionName, "MakeFlag", StringComparison.Ordinal))
                    .ToArray()
            };

            Assert.DoesNotContain(
                prunedTypeModel.ObjectCreations,
                static record => string.Equals(record.EnclosingFunctionName, "MakeFlag", StringComparison.Ordinal));

            var rootModule = Assert.Single(loadedModules.Modules.Values, static module => module.Reference.IsRoot);
            var modulesWithPackageImage = loadedModules.Modules.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            modulesWithPackageImage["PkgFacts"] = rootModule with
            {
                Reference = new ResolvedModuleReference(
                    "PkgFacts",
                    rootModule.Reference.FilePath,
                    IsExternal: false,
                    IsRoot: false),
                PackageImageFacts = remappedPackageImageFacts
            };
            var loadedModulesWithPackageImage = loadedModules with { Modules = modulesWithPackageImage };

            var state = new CompilationState(
                new CompilationInput(moduleSource, Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(StopAfterPassId: "lower-hir"));
            var lowerer = new MidLevelIrLowerer(new CompilerPassContext(state), loadedModulesWithPackageImage, moduleGraph, prunedTypeModel, enumLayoutModel);
            var mir = lowerer.Lower(hir);

            var specialized = Assert.Single(mir.Functions, static function => function.Name == "__stark_mono_fn_Demo__MakeFlag__i32");
            Assert.True(specialized.SupportsDirectCodeGeneration);
            Assert.Contains(
                specialized.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue);
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
    public void LowerMirRewritesGenericCallsToMaterializedSpecializationSymbols()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32 Run(i32 value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "lower-mir"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);

        var run = Assert.Single(mir.Functions, static function => function.Name == "Run");
        var call = run.Blocks
            .SelectMany(static block => block.Statements)
            .Select(static statement => statement.Value)
            .OfType<MidLevelIrCallRValue>()
            .Single();

        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", call.FunctionName);
    }

    [Fact]
    public void OptimizeSsaPreservesMaterializedGenericCallSymbols()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32 Run(i32 value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "optimize-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var call = run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>()
            .Single();

        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", call.FunctionName);
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
                    i32 Left;
                    i32 Right;
                }

                alias HiddenPair = Pair;
                public alias PublicPair = HiddenPair;
                export alias ExportedPair = HiddenPair;
                public alias BufferView<T> = T[];

                public finite law i32 Left(PublicPair value, BufferView<i32> view) {
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

                    fn i32 Run() {
                        stack i32[1] values = { 4 };
                        stack Facade.ExportedPair exported = new Facade.Pair() { Left = 3, Right = 0 };
                        stack Facade.PublicPair pair = exported;
                        stack Facade.BufferView<i32> view = (i32[])values;
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
                    i32 Left;
                    i32 Right;
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

                    fn i32 Run() {
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
    public void ManifestBackedConcreteGenericAliasesMaterializeObjectInitializersAndGroupedConditionalsInMir()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-alias-generic-mir-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box<T> {
                    T Value;
                }

                public alias IntBox = Box<i32>;
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

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

                    fn Facade.IntBox Make(i32 value) {
                        stack Facade.IntBox box = { Value = value };
                        return box;
                    }

                    fn Facade.IntBox Choose(bool takeLeft) {
                        stack Facade.IntBox left = { Value = 1 };
                        stack Facade.IntBox right = { Value = 2 };
                        return (takeLeft ? left : right);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "lower-mir",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
            Assert.NotNull(mir);

            var make = Assert.Single(mir.Functions, static function => function.Name == "Make");
            var choose = Assert.Single(mir.Functions, static function => function.Name == "Choose");

            Assert.True(make.SupportsDirectCodeGeneration);
            Assert.True(choose.SupportsDirectCodeGeneration);
            Assert.Contains(
                make.Blocks.SelectMany(static block => block.Statements).Select(static statement => statement.Value),
                static value => value is MidLevelIrInsertFieldRValue { FieldName: "Value", FieldIndex: 0 });
            Assert.Equal(
                2,
                choose.Blocks
                    .SelectMany(static block => block.Statements)
                    .Count(static statement => statement.Value is MidLevelIrInsertFieldRValue { FieldName: "Value", FieldIndex: 0 }));
            Assert.Contains(choose.Blocks, static block => block.Terminator.Kind == MidLevelIrTerminatorKind.Branch);
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
    public void ManifestBackedGlobalsResolveFromPackageImageFactsWhenBridgeGlobalSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-global-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public const i32 Answer = 42;
                public static mut i32 Counter = 0;
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
            Assert.Contains("Facade.Answer", importedDocument.PackageImageFacts!.Globals.Keys);
            Assert.Contains("Facade.Counter", importedDocument.PackageImageFacts.Globals.Keys);

            var corruptedSourceText = importedDocument.ParseResult.SourceText
                .Replace("public const i32 Answer = 0;", "public const Missing Answer = 0;", StringComparison.Ordinal)
                .Replace("public static mut i32 Counter;", "public static mut Missing Counter;", StringComparison.Ordinal);
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

                    fn i32 Run() {
                        return Facade.Answer;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.Equal("i32", typeCheckModel.Globals["Facade.Answer"].Type.DisplayName);
            Assert.True(typeCheckModel.Globals["Facade.Answer"].IsConst);
            Assert.Equal("i32", typeCheckModel.Globals["Facade.Counter"].Type.DisplayName);
            Assert.True(typeCheckModel.Globals["Facade.Counter"].IsMutable);
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
    public void ManifestBackedNamedTypeShapeResolvesFromPackageImageFactsWhenBridgeTypeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-type-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32 Value;
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
            Assert.Contains("Facade.Box", importedDocument.PackageImageFacts!.NamedTypes.Keys);
            Assert.True(importedDocument.PackageImageFacts.NamedTypes["Facade.Box"].TryGetField("Value", out var field, out _));
            Assert.Equal("i32", field.Type.DisplayName);

            var corruptedSourceText = importedDocument.ParseResult.SourceText.Replace(
                "i32 Value;",
                "Missing Wrong;",
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

                    fn i32 Run() {
                        stack Facade.Box box = new Facade.Box() { Value = 3 };
                        return box.Value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.NamedTypes["Facade.Box"].TryGetField("Value", out var importedField, out _));
            Assert.Equal("i32", importedField.Type.DisplayName);
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
    public void ManifestBackedRecordPrimaryConstructorsResolveFromPackageImageFactsWhenBridgeTypeSourceIsCorrupted()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-constructor-facts-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32 Value) {
                    i32 Count;
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
            Assert.Contains("Facade.Counter", importedDocument.PackageImageFacts!.Constructors.Keys);
            var primaryConstructor = Assert.Single(importedDocument.PackageImageFacts.Constructors["Facade.Counter"]);
            Assert.True(primaryConstructor.IsPrimaryShape);
            Assert.Equal("i32", Assert.Single(primaryConstructor.Parameters).Type.DisplayName);

            var corruptedSourceText = importedDocument.ParseResult.SourceText.Replace(
                "record Counter(i32 Value)",
                "record Counter(Missing Value)",
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

                    fn i32 Run(i32 value) {
                        stack Facade.Counter counter = new Facade.Counter(value);
                        return counter.Value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new DocumentOnlyModuleResolver(corruptedDocument)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            var objectCreation = Assert.Single(typeCheckModel.ObjectCreations);
            Assert.NotNull(objectCreation.Constructor);
            Assert.True(objectCreation.Constructor!.IsPrimaryShape);
            Assert.Equal("i32", Assert.Single(objectCreation.Constructor.Parameters).Type.DisplayName);
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
    public void ManifestBackedExplicitStructConstructorsResolveFromPackageImageFactsWithoutBridgeDeclarations()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-explicit-struct-constructors-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public struct Box {
                    i32 Value;

                    Box(i32 value) {
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
                manifestPath,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Box", importedDocument.PackageImageFacts!.Constructors.Keys);
            var explicitConstructor = Assert.Single(importedDocument.PackageImageFacts.Constructors["Facade.Box"]);
            Assert.False(explicitConstructor.IsPrimaryShape);
            Assert.Equal("i32", Assert.Single(explicitConstructor.Parameters).Type.DisplayName);
            Assert.DoesNotContain("Box(i32 value)", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn void Run(i32 value) {
                        stack Facade.Box box = new Facade.Box(value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            var objectCreation = Assert.Single(typeCheckModel.ObjectCreations);
            Assert.NotNull(objectCreation.Constructor);
            Assert.False(objectCreation.Constructor!.IsPrimaryShape);
            Assert.Equal("i32", Assert.Single(objectCreation.Constructor.Parameters).Type.DisplayName);
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
    public void ManifestBackedExplicitRecordConstructorsResolveFromPackageImageFactsWithoutBridgeDeclarations()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-explicit-record-constructors-pipeline-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public record Counter(i32 Value) {
                    i32 Count;

                    Counter(i32 value, i32 count) {
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
                manifestPath,
                Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a"),
                manifest,
                facadeModule);

            Assert.True(PackageImageLoader.TryBuildModuleDocument(resolvedPackageModule, out var importedDocument));
            Assert.NotNull(importedDocument.PackageImageFacts);
            Assert.Contains("Facade.Counter", importedDocument.PackageImageFacts!.Constructors.Keys);
            Assert.Collection(
                importedDocument.PackageImageFacts.Constructors["Facade.Counter"],
                primaryConstructor =>
                {
                    Assert.True(primaryConstructor.IsPrimaryShape);
                    Assert.Equal("i32", Assert.Single(primaryConstructor.Parameters).Type.DisplayName);
                },
                explicitConstructor =>
                {
                    Assert.False(explicitConstructor.IsPrimaryShape);
                    Assert.Equal(2, explicitConstructor.Parameters.Count);
                    Assert.All(explicitConstructor.Parameters, static parameter => Assert.Equal("i32", parameter.Type.DisplayName));
                });
            Assert.DoesNotContain("Counter(i32 value, i32 count)", importedDocument.ParseResult.SourceText, StringComparison.Ordinal);

            File.WriteAllText(manifestPath, manifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32 Run(i32 value) {
                        stack Facade.Counter counter = new Facade.Counter(value, 7);
                        return counter.Value;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    StopAfterPassId: "type-check",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            var objectCreation = Assert.Single(typeCheckModel.ObjectCreations);
            Assert.NotNull(objectCreation.Constructor);
            Assert.False(objectCreation.Constructor!.IsPrimaryShape);
            Assert.Equal(2, objectCreation.Constructor.Parameters.Count);
            Assert.All(objectCreation.Constructor.Parameters, static parameter => Assert.Equal("i32", parameter.Type.DisplayName));
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

                internal alias Hidden = i32;

                public finite law i32 Id(i32 value) {
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

                    fn i32 Run() {
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

    private sealed class ThrowingPass : ICompilerPass
    {
        public string Id => "throwing-pass";

        public CompilerPhase Phase => CompilerPhase.Parsing;

        public PassExecutionMode ExecutionMode => PassExecutionMode.RunAlways;

        public IReadOnlyList<string> Dependencies => [];

        public void Execute(CompilerPassContext context)
        {
            throw new InvalidOperationException("boom");
        }
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
                    i32 Value;

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

                    fn i32 Run() {
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

    private static StarkPackageModuleManifest WithEffectiveLegacyCompilerSectionCopies(StarkPackageModuleManifest module)
    {
        return module with
        {
            TypedInterface = module.EffectiveTypedInterface,
            CompilerFacts = module.EffectiveCompilerFacts,
            GenericTemplates = module.EffectiveGenericTemplates
        };
    }

    private sealed class DocumentOnlyModuleResolver : IModuleSourceResolver, IModuleDocumentResolver
    {
        private readonly Dictionary<string, LoadedModuleDocument> _documents;

        public DocumentOnlyModuleResolver(params LoadedModuleDocument[] documents)
        {
            _documents = documents.ToDictionary(static document => document.Reference.ModuleName, StringComparer.Ordinal);
        }

        public int SourceLoadAttempts { get; private set; }

        public bool TryResolveModule(string moduleName, out ResolvedModuleReference module)
        {
            if (_documents.TryGetValue(moduleName, out var document))
            {
                module = document.Reference;
                return true;
            }

            module = default!;
            return false;
        }

        public bool TryLoadModuleSource(ResolvedModuleReference module, out string sourceText, out string? filePath)
        {
            SourceLoadAttempts++;
            sourceText = string.Empty;
            filePath = null;
            return false;
        }

        public bool TryLoadModuleDocument(ResolvedModuleReference module, LlvmTargetInfo? targetInfo, out LoadedModuleDocument document)
        {
            _ = targetInfo;
            return _documents.TryGetValue(module.ModuleName, out document!);
        }
    }
}
