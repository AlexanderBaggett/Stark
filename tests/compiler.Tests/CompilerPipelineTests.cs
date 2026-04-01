using Stark.Compiler;

namespace compiler.Tests;

public sealed class CompilerPipelineTests
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
        Assert.Equal(20, result.Executions.Count(static execution => execution.Status == PassExecutionStatus.Executed));
        Assert.Contains(
            result.Logs,
            log => log.Severity == DiagnosticSeverity.Info
                && log.Category == "pipeline"
                && log.EventId == "pass-completed"
                && log.Stage == "emit-llvm"
                && log.Data is not null
                && log.Data.TryGetValue("status", out var status)
                && string.Equals(status, PassExecutionStatus.Executed.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void FunctionKindsAndModifiersDeriveExpectedEffectProfiles()
    {
        var pipeline = DefaultCompilerPipeline.Create();

        var result = pipeline.Run(new CompilationInput(
            """
            module Effects

            public finite law i32 Add(i32 left, i32 right);
            export ffi cold fn void Accept(rawptr<i8> value);
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

        var accept = effectModel.Functions["Accept"];
        Assert.False(accept.IsPure);
        Assert.False(accept.UseFastCallingConvention);
        Assert.False(accept.NoUnwind);
        Assert.True(accept.IsFfi);
        Assert.True(accept.IsCold);
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

            fn i32 Run(i32[2] values, i32 index) {
                return values[index];
            }
            """));

        Assert.True(result.Succeeded);
        var log = Assert.Single(result.Logs, log =>
            log.Category == "lowering"
            && log.EventId == "unsupported-lowering"
            && log.Stage == "lower-mir"
            && log.SymbolName == "Run"
            && log.Operation == "LowerIndexAccess");

        Assert.Equal(DiagnosticSeverity.Warning, log.Severity);
        Assert.NotNull(log.Data);
        Assert.Equal("Demo", log.Data["module"]);
        Assert.Equal("Dynamic fixed-array indexing currently requires a local fixed array source.", log.Data["reason"]);
        Assert.Equal("StarkCfg", log.Data["bodyLoweringKind"]);
    }

    [Fact]
    public void LlvmDeclarationFallbackProducesStructuredWarningLogs()
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
        var log = Assert.Single(result.Logs, log =>
            log.Category == "codegen"
            && log.EventId == "llvm-body-fallback"
            && log.Stage == "emit-llvm"
            && log.SymbolName == "Run");

        Assert.Equal(DiagnosticSeverity.Warning, log.Severity);
        Assert.NotNull(log.Data);
        Assert.Equal("Demo", log.Data["module"]);
        Assert.Equal("StarkCfg", log.Data["bodyLoweringKind"]);
        Assert.Equal("True", log.Data["supportsDirectCodeGeneration"]);
        Assert.Contains("Local storage class 'heap'", log.Data["reason"], StringComparison.Ordinal);
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
    public async Task ManifestBackedLibrariesResolveWithoutSourceFiles()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out _))
        {
            return;
        }

        var tempDirectory = Directory.CreateTempSubdirectory("stark-manifest-pipeline-");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            await File.WriteAllTextAsync(
                mathPath,
                """
                module Math

                public finite law i32 Add(i32 left, i32 right) {
                    return left + right;
                }
                """);

            await File.WriteAllTextAsync(
                facadePath,
                """
                export import Math
                module Facade

                public finite law i32 Double(i32 value) {
                    return Math.Add(value, value);
                }
                """);

            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [facadePath, "--emit-lib", "-o", libraryPath],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.Equal(0, buildExitCode);
            Assert.Contains("pipeline:pass-started", buildStderr.ToString(), StringComparison.Ordinal);

            File.Delete(facadePath);
            File.Delete(mathPath);

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
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

            Assert.True(result.Succeeded);
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.ModuleGraph, out ModuleGraph? moduleGraph));
            Assert.NotNull(moduleGraph);
            Assert.True(moduleGraph.HasModule("Facade"));
            Assert.True(moduleGraph.HasModule("Math"));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
            Assert.NotNull(typeCheckModel);
            Assert.True(typeCheckModel.Functions.ContainsKey("Math.Add"));
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

            var manifest = PackageManifestBuilder.Create(libraryResult, libraryPath);
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

            var manifest = PackageManifestBuilder.Create(libraryResult, libraryPath);
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

            var manifest = PackageManifestBuilder.Create(
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

            var manifest = PackageManifestBuilder.Create(
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

            var manifest = PackageManifestBuilder.Create(
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

            var manifest = PackageManifestBuilder.Create(
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
}
