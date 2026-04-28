using Stark.Compiler;
using Stark.Parsing;
using static compiler.PipelineTests.CompilerPipelineTestSupport;

namespace compiler.PipelineTests;

public sealed class CompilerPipelineOptimizeSsaTests
{
    [Fact]
    public void CallableAddressTakenFactsArePrunedAfterDirectCallDevirtualization()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                noinline finite law i32[-2147483648 2147483647] Target() {
                    return 1;
                }

                fn i32[-2147483648 2147483647] Run() {
                    stack fnptr<fn i32[-2147483648 2147483647]()> op = Target;
                    return op();
                }
                """),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));

        Assert.NotNull(typeModel);
        Assert.NotNull(hir);
        Assert.NotNull(mir);
        Assert.NotNull(ssa);
        Assert.NotNull(optimizedSsa);
        Assert.NotNull(llvm);

        Assert.Equal("Target", Assert.Single(typeModel.AddressTakenFunctions).Signature.Name);
        Assert.Equal("Target", Assert.Single(hir.AddressTakenFunctions));
        Assert.Equal("Target", Assert.Single(mir.AddressTakenFunctions));
        Assert.Equal("Target", Assert.Single(ssa.AddressTakenFunctions));
        Assert.Empty(optimizedSsa.AddressTakenFunctions);
        Assert.Empty(llvm.AddressTakenFunctions);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var directCall = Assert.Single(run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>());

        Assert.Equal("Target", directCall.FunctionName);
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions),
            static instruction => instruction is SsaValueInstruction { Value: SsaIndirectCallRValue });

        var executedPassIds = result.Executions
            .Where(static execution => execution.Status == PassExecutionStatus.Executed)
            .Select(static execution => execution.PassId)
            .ToArray();
        Assert.True(
            Array.IndexOf(executedPassIds, "devirt-ssa") < Array.IndexOf(executedPassIds, "inline-ssa"),
            "Expected direct-call devirtualization to run before Stark-level inlining.");
    }

    [Fact]
    public void NonCapturingLambdaAddressTakenFactsArePrunedAfterDirectCallDevirtualization()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run() {
                    stack fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> increment =
                        (i32[-2147483648 2147483647] value) => value + 1;
                    return increment(41);
                }
                """),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.HighLevelIr, out HighLevelIrModule? hir));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));

        Assert.NotNull(typeModel);
        Assert.NotNull(hir);
        Assert.NotNull(mir);
        Assert.NotNull(ssa);
        Assert.NotNull(optimizedSsa);
        Assert.NotNull(llvm);

        var lambdaName = Assert.Single(typeModel.Lambdas).FunctionName;
        Assert.Empty(typeModel.AddressTakenFunctions);
        Assert.Equal(lambdaName, Assert.Single(hir.AddressTakenFunctions));
        Assert.Equal(lambdaName, Assert.Single(mir.AddressTakenFunctions));
        Assert.Equal(lambdaName, Assert.Single(ssa.AddressTakenFunctions));
        Assert.Empty(optimizedSsa.AddressTakenFunctions);
        Assert.Empty(llvm.AddressTakenFunctions);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var directCall = Assert.Single(run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>());

        Assert.Equal(lambdaName, directCall.FunctionName);
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions),
            static instruction => instruction is SsaValueInstruction { Value: SsaIndirectCallRValue });
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

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "devirt-ssa"));

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
    public void OptimizeSsaDevirtualizesIdenticalFunctionPointerPhiTargets()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                noinline finite law i32[-2147483648 2147483647] Target(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                fn i32[-2147483648 2147483647] Run(bool flag, i32[-2147483648 2147483647] value) {
                    stack mut fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> op = Target;
                    if (flag) {
                        op = Target;
                    } else {
                        op = Target;
                    }

                    return op(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "devirt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var directCall = Assert.Single(run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>());

        Assert.Equal("Target", directCall.FunctionName);
        Assert.Empty(ssa.AddressTakenFunctions);
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions),
            static instruction => instruction is SsaValueInstruction { Value: SsaIndirectCallRValue });
    }

    [Fact]
    public void OptimizeSsaKeepsMixedFunctionPointerPhiTargetsIndirect()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                finite law i32[-2147483648 2147483647] Target(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                finite law i32[-2147483648 2147483647] Other(i32[-2147483648 2147483647] value) {
                    return value - 1;
                }

                fn i32[-2147483648 2147483647] Run(bool flag, i32[-2147483648 2147483647] value) {
                    stack mut fnptr<fn i32[-2147483648 2147483647](i32[-2147483648 2147483647])> op = Target;
                    if (flag) {
                        op = Target;
                    } else {
                        op = Other;
                    }

                    return op(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "optimize-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        Assert.Equal(
            new[] { "Other", "Target" },
            ssa.AddressTakenFunctions.OrderBy(static functionName => functionName).ToArray());

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var indirectCall = Assert.Single(run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaIndirectCallRValue>());
        var targetReference = Assert.IsType<SsaValueReference>(indirectCall.Target);
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Phis),
            phi => string.Equals(phi.ResultName, targetReference.Name, StringComparison.Ordinal));

        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            static call => call.FunctionName is "Target" or "Other");
    }

    [Fact]
    public void DevirtualizeSsaSkipsAtO0()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                noinline finite law i32[-2147483648 2147483647] Target() {
                    return 1;
                }

                fn i32[-2147483648 2147483647] Run() {
                    stack fnptr<fn i32[-2147483648 2147483647]()> op = Target;
                    return op();
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "devirt-ssa",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        Assert.Equal("Target", Assert.Single(ssa.AddressTakenFunctions));

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions),
            static instruction => instruction is SsaValueInstruction { Value: SsaIndirectCallRValue });
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            static call => call.FunctionName == "Target");
    }

    [Fact]
    public void CleanupSsaRemovesSourceLevelIntegerAlgebraicIdentities()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i64[-9223372036854775808 9223372036854775807] Run(i64[-9223372036854775808 9223372036854775807] value) {
                    stack i64[-9223372036854775808 9223372036854775807] add = value + 0;
                    stack i64[-9223372036854775808 9223372036854775807] multiply = add * 1;
                    stack i64[-9223372036854775808 9223372036854775807] masked = multiply & -1;
                    stack i64[-9223372036854775808 9223372036854775807] shifted = masked << 0;
                    return shifted ^ 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "cleanup-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaBinaryRValue>(),
            static binary => binary.Operator is SsaBinaryOperator.Add
                or SsaBinaryOperator.Multiply
                or SsaBinaryOperator.BitwiseAnd
                or SsaBinaryOperator.ShiftLeft
                or SsaBinaryOperator.BitwiseXor);
    }

    [Fact]
    public void CleanupSsaRemovesIntegerAlgebraicAbsorbingAndSameOperandIdentities()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i64[-9223372036854775808 9223372036854775807] Run(i64[-9223372036854775808 9223372036854775807] value) {
                    stack i64[-9223372036854775808 9223372036854775807] sameAnd = value & value;
                    stack i64[-9223372036854775808 9223372036854775807] sameOr = sameAnd | sameAnd;
                    stack i64[-9223372036854775808 9223372036854775807] zeroXor = sameOr ^ sameOr;
                    stack i64[-9223372036854775808 9223372036854775807] zeroAnd = value & 0;
                    stack i64[-9223372036854775808 9223372036854775807] zeroMultiply = value * 0;
                    stack i64[-9223372036854775808 9223372036854775807] zeroSubtract = value - value;
                    stack i64[-9223372036854775808 9223372036854775807] allOnes = value | -1;
                    return zeroXor + zeroAnd + zeroMultiply + zeroSubtract + (allOnes & 1);
                }
                """),
            new CompilerOptions(StopAfterPassId: "cleanup-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaBinaryRValue>(),
            static binary => binary.Operator is SsaBinaryOperator.Subtract
                or SsaBinaryOperator.Multiply
                or SsaBinaryOperator.BitwiseAnd
                or SsaBinaryOperator.BitwiseOr
                or SsaBinaryOperator.BitwiseXor);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(1), resultValue.Value);
    }

    [Fact]
    public void InlineSsaInlinesSmallDirectCallsAndRerunsConstantPropagation()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                inline finite law i32[-2147483648 2147483647] AddOne(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                fn i32[-2147483648 2147483647] Run() {
                    return AddOne(41);
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            static call => call.FunctionName == "AddOne");

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(42), resultValue.Value);
    }

    [Fact]
    public void InlineSsaInlinesSmallModulePrivateDirectCallsWithoutExplicitInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                finite law i32[-2147483648 2147483647] AddOne(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                fn i32[-2147483648 2147483647] Run() {
                    return AddOne(41);
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            static call => call.FunctionName == "AddOne");

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(42), resultValue.Value);
    }

    [Fact]
    public void InlineSsaInlinesSmallMonomorphizedGenericHelpers()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value) {
                    return value;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            static call => call.FunctionName == "__stark_mono_fn_Demo__Identity__i32");
    }

    [Fact]
    public void InlineSsaOptimizesSmallGenericAbstractionLikeHandWrittenCode()
    {
        var handWritten = CompileRunFunction(
            """
            module Demo

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                return value;
            }
            """);
        var generic = CompileRunFunction(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                return Identity(value);
            }
            """);

        AssertEquivalentSingleBlockReturn(handWritten);
        AssertEquivalentSingleBlockReturn(generic);

        static SsaFunction CompileRunFunction(string source)
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(source),
                new CompilerOptions(StopAfterPassId: "inline-ssa"));

            Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
            Assert.NotNull(ssa);
            return Assert.Single(ssa.Functions, static function => function.Name == "Run");
        }

        static void AssertEquivalentSingleBlockReturn(SsaFunction function)
        {
            var block = Assert.Single(function.Blocks);
            Assert.Empty(block.Phis);
            Assert.Empty(block.Instructions);
            var returned = Assert.IsType<SsaValueReference>(block.Terminator.Value);
            Assert.Equal("arg_value", returned.Name);
        }
    }

    [Fact]
    public void InlineSsaKeepsExplicitNoInlineMonomorphizedGenericHelpers()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                noinline fn T Identity<T>(T value) {
                    return value;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return Identity(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var call = Assert.Single(run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>());

        Assert.Equal("__stark_mono_fn_Demo__Identity__i32", call.FunctionName);
    }

    [Fact]
    public void InlineSsaKeepsPublicOrdinaryDirectCallsWithoutExplicitInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public fn i32[-2147483648 2147483647] AddOne(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return AddOne(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var call = Assert.Single(run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>());

        Assert.Equal("AddOne", call.FunctionName);
    }

    [Fact]
    public void InlineSsaInlinesSmallPublicLawDirectCallsWithoutExplicitInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public finite law i32[-2147483648 2147483647] AddOne(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                fn i32[-2147483648 2147483647] Run() {
                    return AddOne(41);
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            static call => call.FunctionName == "AddOne");

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(42), resultValue.Value);
    }

    [Fact]
    public void InlineSsaInlinesDirectCallForwarderChains()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                inline finite law i32[-2147483648 2147483647] AddOne(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                inline finite law i32[-2147483648 2147483647] Forward(i32[-2147483648 2147483647] value) {
                    return AddOne(value);
                }

                fn i32[-2147483648 2147483647] Run() {
                    return Forward(41);
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            static call => call.FunctionName is "Forward" or "AddOne");

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(42), resultValue.Value);
    }

    [Fact]
    public void InlineSsaKeepsExplicitNoInlineDirectCalls()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                noinline finite law i32[-2147483648 2147483647] AddOne(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return AddOne(value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var call = Assert.Single(run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>());

        Assert.Equal("AddOne", call.FunctionName);
    }

    [Fact]
    public void InlineSsaSkipsAtO0()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                inline finite law i32[-2147483648 2147483647] AddOne(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    return AddOne(value);
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "inline-ssa",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var call = Assert.Single(run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>());

        Assert.Equal("AddOne", call.FunctionName);
    }

    [Fact]
    public void ValueFactsCaptureIntegerRangesAndProvenComparisons()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn bool Run(i32[0 10] value) {
                    return value < 20;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        var valueFacts = runFacts.Values["arg_value"];

        Assert.Equal(SsaFactLatticeKind.Known, valueFacts.IntegerRangeKind);
        Assert.NotNull(valueFacts.IntegerRange);
        Assert.Equal(new System.Numerics.BigInteger(0), valueFacts.IntegerRange!.Min);
        Assert.Equal(new System.Numerics.BigInteger(10), valueFacts.IntegerRange.Max);

        Assert.Contains(
            runFacts.Values.Values,
            static fact => fact.BooleanKind == SsaFactLatticeKind.Known
                           && fact.BooleanConstant == true);
    }

    [Fact]
    public void ValueFactsJoinIntegerRangesAtPhis()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[0 10] Choose(bool flag, i32[2 3] left, i32[4 5] right) {
                    stack mut i32[0 10] result = left;
                    if (flag) {
                        result = right;
                    }

                    return result;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var chooseFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Choose");
        var phiFacts = Assert.Single(
            chooseFacts.Values.Values,
            static fact => fact.ValueName.EndsWith("_phi", StringComparison.Ordinal));

        Assert.Equal(SsaFactLatticeKind.Known, phiFacts.IntegerRangeKind);
        Assert.NotNull(phiFacts.IntegerRange);
        Assert.Equal(new System.Numerics.BigInteger(2), phiFacts.IntegerRange!.Min);
        Assert.Equal(new System.Numerics.BigInteger(5), phiFacts.IntegerRange.Max);
    }

    [Fact]
    public void ValueFactsPropagateJoinedRangesThroughDependentInstructions()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] AddAfterJoin(bool flag, i32[0 10] left, i32[20 30] right) {
                    stack mut i32[-2147483648 2147483647] value = left;
                    if (flag) {
                        value = right;
                    }

                    return value + 1;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "AddAfterJoin");
        Assert.Contains(
            runFacts.Values.Values,
            static fact => fact.IntegerRangeKind == SsaFactLatticeKind.Known
                           && fact.IntegerRange is { Min: var min, Max: var max }
                           && min == new System.Numerics.BigInteger(1)
                           && max == new System.Numerics.BigInteger(31));
    }

    [Fact]
    public void ValueFactsPropagateBitwiseAndShiftRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[0 255] Combine(i32[0 15] value, i32[0 2] amount) {
                    stack i32[0 255] masked = (i32[0 255])(value & 7);
                    stack i32[0 255] shifted = (i32[0 255])(value << 2);
                    stack i32[0 255] restored = (i32[0 255])(shifted >> amount);
                    return (i32[0 255])(masked | restored);
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var combineFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Combine");
        Assert.Contains(combineFacts.Values.Values, static fact => HasIntegerRange(fact, 0, 7));
        Assert.Contains(combineFacts.Values.Values, static fact => HasIntegerRange(fact, 0, 60));
        Assert.Contains(combineFacts.Values.Values, static fact => HasIntegerRange(fact, 0, 63));
    }

    [Fact]
    public void ValueFactsUseKnownBitsToProveMaskedSingletons()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[0 255] Mask(i32[0 7] value) {
                    return (i32[0 255])(value & 8);
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var maskFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Mask");
        Assert.Contains(maskFacts.Values.Values, static fact => HasIntegerRange(fact, 0, 0));
    }

    [Fact]
    public void ValueFactsUseKnownBitsToProveShiftedLowBitIsZero()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[0 255] MaskShifted(i32[0 7] value) {
                    stack i32[0 255] shifted = (i32[0 255])(value << 1);
                    return (i32[0 255])(shifted & 1);
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var maskFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "MaskShifted");
        Assert.Contains(maskFacts.Values.Values, static fact => HasIntegerRange(fact, 0, 0));
    }

    [Fact]
    public void ValueFactsUseKnownBitsToProveEqualityFalse()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn bool ForcedBitCannotEqualZero(i32[0 7] value) {
                    stack i32[0 4095] forced = (i32[0 4095])(value | 8);
                    return forced == 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "ForcedBitCannotEqualZero");
        Assert.Contains(
            runFacts.Values.Values,
            static fact => fact.BooleanKind == SsaFactLatticeKind.Known
                           && fact.BooleanConstant == false);
    }

    [Fact]
    public void FactDrivenBranchPruningUsesKnownBitsForMaskedComparisons()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 7] value) {
                    stack i32[0 255] masked = (i32[0 255])(value & 8);
                    if (masked == 0) {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(1), resultValue.Value);
    }

    [Fact]
    public void FactDrivenBranchPruningUsesKnownBitsForImpossibleEquality()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 7] value) {
                    stack i32[0 4095] forced = (i32[0 4095])(value | 8);
                    if (forced == 0) {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(2), resultValue.Value);
    }

    [Fact]
    public void FactDrivenBranchPruningUsesKnownBitsForShiftedMaskedComparisons()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 7] value) {
                    stack i32[0 255] shifted = (i32[0 255])(value << 1);
                    stack i32[0 255] masked = (i32[0 255])(shifted & 1);
                    if (masked == 0) {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(1), resultValue.Value);
    }

    [Fact]
    public void ValueFactsCaptureBranchTargetEntryRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 100] value) {
                    if (value < 10) {
                        return value;
                    }

                    return 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(ssa);
        Assert.NotNull(facts);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var entry = Assert.Single(run.Blocks, block => block.Id == run.EntryBlockId);
        var trueTarget = entry.Terminator.Targets[0];
        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.NotNull(runFacts.BlockEntryValueFacts);
        Assert.True(runFacts.BlockEntryValueFacts!.TryGetValue(trueTarget, out var trueEntryFacts));
        Assert.Contains(trueEntryFacts.Values, static valueFacts => HasIntegerRange(valueFacts, 0, 9));
    }

    [Fact]
    public void ValueFactsCaptureBranchTargetEntryNullability()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(rawptr<i32[-2147483648 2147483647]> ptr) {
                    if (ptr != null) {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(ssa);
        Assert.NotNull(facts);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var entry = Assert.Single(run.Blocks, block => block.Id == run.EntryBlockId);
        var trueTarget = entry.Terminator.Targets[0];
        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.NotNull(runFacts.BlockEntryValueFacts);
        Assert.True(runFacts.BlockEntryValueFacts!.TryGetValue(trueTarget, out var trueEntryFacts));
        Assert.Contains(
            trueEntryFacts.Values,
            static valueFacts => valueFacts.Type.Kind == StarkTypeKind.RawPointer
                                 && valueFacts.Nullability == SsaNullabilityFactKind.NonNull);
    }

    [Fact]
    public void ValueFactsCapturePointerEqualityTargetEntryNullability()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(rawmutptr<i32[-2147483648 2147483647]> ptr) {
                    stack mut i32[-2147483648 2147483647] local = 1;
                    stack rawmutptr<i32[-2147483648 2147483647]> localPtr = &local;

                    if (ptr == localPtr) {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(ssa);
        Assert.NotNull(facts);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var entry = Assert.Single(run.Blocks, block => block.Id == run.EntryBlockId);
        var trueTarget = entry.Terminator.Targets[0];
        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.NotNull(runFacts.BlockEntryValueFacts);
        Assert.True(runFacts.BlockEntryValueFacts!.TryGetValue(trueTarget, out var trueEntryFacts));
        Assert.Contains(
            trueEntryFacts.Values,
            static valueFacts => valueFacts.ValueName == "arg_ptr"
                                 && valueFacts.Type.Kind == StarkTypeKind.RawPointer
                                 && valueFacts.Nullability == SsaNullabilityFactKind.NonNull
                                 && valueFacts.PointerAlignmentKind == SsaFactLatticeKind.Known
                                 && valueFacts.PointerAlignmentBytes == 4);
    }

    [Fact]
    public void ValueFactsCapturePointerInequalityFalseTargetEntryNullability()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(rawmutptr<i32[-2147483648 2147483647]> ptr) {
                    stack mut i32[-2147483648 2147483647] local = 1;
                    stack rawmutptr<i32[-2147483648 2147483647]> localPtr = &local;

                    if (ptr != localPtr) {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(ssa);
        Assert.NotNull(facts);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var entry = Assert.Single(run.Blocks, block => block.Id == run.EntryBlockId);
        var falseTarget = entry.Terminator.Targets[1];
        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.NotNull(runFacts.BlockEntryValueFacts);
        Assert.True(runFacts.BlockEntryValueFacts!.TryGetValue(falseTarget, out var falseEntryFacts));
        Assert.Contains(
            falseEntryFacts.Values,
            static valueFacts => valueFacts.ValueName == "arg_ptr"
                                 && valueFacts.Type.Kind == StarkTypeKind.RawPointer
                                 && valueFacts.Nullability == SsaNullabilityFactKind.NonNull);
    }

    [Fact]
    public void ValueFactsCapturePointerAlignmentForAddressValues()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run() {
                    stack mut i32[-2147483648 2147483647] value = 1;
                    stack rawmutptr<i32[-2147483648 2147483647]> ptr = &value;
                    return *ptr;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.Contains(
            runFacts.Values.Values,
            static valueFacts => valueFacts.Type.Kind == StarkTypeKind.RawPointer
                                 && valueFacts.Nullability == SsaNullabilityFactKind.NonNull
                                 && valueFacts.PointerAlignmentKind == SsaFactLatticeKind.Known
                                 && valueFacts.PointerAlignmentBytes == 4);
    }

    [Fact]
    public void ValueFactsCaptureFixedArraySliceLengths()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 2] index) {
                    stack i32[-2147483648 2147483647][3] values = { 4, 7, 9 };
                    stack i32[-2147483648 2147483647][] view = values;
                    return view[index];
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        var sliceFacts = Assert.Single(
            runFacts.Values.Values,
            static fact => fact.Type.Kind == StarkTypeKind.Slice
                           && fact.LengthKind == SsaFactLatticeKind.Known
                           && fact.LengthRange is { Min: var min, Max: var max }
                           && min == new System.Numerics.BigInteger(3)
                           && max == new System.Numerics.BigInteger(3));

        Assert.Equal(SsaFactLatticeKind.Known, sliceFacts.LengthKind);
    }

    [Fact]
    public void ValueFactsJoinSliceLengthRangesAtPhis()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Read(bool flag, i32[0 1] index) {
                    stack i32[-2147483648 2147483647][3] left = { 1, 2, 3 };
                    stack i32[-2147483648 2147483647][5] right = { 4, 5, 6, 7, 8 };
                    stack mut i32[-2147483648 2147483647][] view = left;
                    if (flag) {
                        view = right;
                    }

                    return view[index];
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Read");
        Assert.Contains(
            runFacts.Values.Values,
            static fact => fact.Type.Kind == StarkTypeKind.Slice
                           && HasLengthRange(fact, 3, 5));
    }

    [Fact]
    public void ValueFactsCaptureTextSliceLengthRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn unicode Run(unicode text, i32[2 5] length) {
                    return text[0, length];
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        var textSliceFacts = Assert.Single(
            runFacts.Values.Values,
            static fact => fact.Type.Kind == StarkTypeKind.Unicode
                           && fact.LengthKind == SsaFactLatticeKind.Known
                           && fact.LengthRange is { Min: var min, Max: var max }
                           && min == new System.Numerics.BigInteger(2)
                           && max == new System.Numerics.BigInteger(5));

        Assert.Equal(SsaFactLatticeKind.Known, textSliceFacts.LengthKind);
    }

    [Fact]
    public void ValueFactsCaptureTextLiteralLengthCallRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                fn i64[0 9223372036854775807] Run() {
                    return AsciiLength("stark") + UnicodeLength((unicode)"llvm");
                }

                public finite law i64[0 9223372036854775807] AsciiLength(ascii source);
                public finite law i64[0 9223372036854775807] UnicodeLength(unicode source);
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.Contains(runFacts.Values.Values, static fact => HasIntegerRange(fact, 5, 5));
        Assert.Contains(runFacts.Values.Values, static fact => HasIntegerRange(fact, 4, 4));
    }

    [Fact]
    public void ValueFactsCaptureExplicitWrappingAndSaturatingArithmeticRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 10] left, i32[0 5] right) {
                    stack i32[-2147483648 2147483647] wrapped = left +% right;
                    stack i32[-2147483648 2147483647] saturated = left +| right;
                    return wrapped + saturated;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.True(runFacts.Values.Values.Count(static fact => HasIntegerRange(fact, 0, 15)) >= 2);
    }

    [Fact]
    public void FactDrivenBranchPruningRemovesProvenBranchAndStalePhiIncoming()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 10] value) {
                    stack mut i32[-2147483648 2147483647] result = 0;
                    if (value < 20) {
                        result = 1;
                    } else {
                        result = 2;
                    }

                    return result;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);
        Assert.DoesNotContain(run.Blocks.SelectMany(static block => block.Phis), static phi => phi.Incomings.Count > 1);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(1), resultValue.Value);
    }

    [Fact]
    public void FactDrivenBranchPruningUsesBranchTargetEntryRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 100] value) {
                    if (value < 10) {
                        if (value >= 10) {
                            return 1;
                        }

                        return 2;
                    }

                    return 3;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Single(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);
        Assert.DoesNotContain(
            run.Blocks,
            static block => block.Terminator.Value is SsaIntegerConstant integer
                            && integer.Value == new System.Numerics.BigInteger(1));
    }

    [Fact]
    public void FactDrivenBranchPruningUsesBranchTargetNullability()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(rawptr<i32[-2147483648 2147483647]> ptr) {
                    if (ptr != null) {
                        if (ptr == null) {
                            return 1;
                        }

                        return 2;
                    }

                    return 3;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Single(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);
        Assert.DoesNotContain(
            run.Blocks,
            static block => block.Terminator.Value is SsaIntegerConstant integer
                            && integer.Value == new System.Numerics.BigInteger(1));
    }

    [Fact]
    public void FactDrivenBranchPruningUsesPointerEqualityNullability()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(rawmutptr<i32[-2147483648 2147483647]> ptr) {
                    stack mut i32[-2147483648 2147483647] local = 1;
                    stack rawmutptr<i32[-2147483648 2147483647]> localPtr = &local;

                    if (ptr == localPtr) {
                        if (ptr == null) {
                            return 1;
                        }

                        return 2;
                    }

                    return 3;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Single(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);
        Assert.DoesNotContain(
            run.Blocks,
            static block => block.Terminator.Value is SsaIntegerConstant integer
                            && integer.Value == new System.Numerics.BigInteger(1));
    }

    [Fact]
    public void FactDrivenBranchPruningUsesBitwiseRangeFacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 15] value) {
                    stack i32[0 255] masked = (i32[0 255])(value & 7);
                    if (masked < 8) {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(1), resultValue.Value);
    }

    [Fact]
    public void FactDrivenBranchPruningUsesExplicitArithmeticRangeFacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 10] left, i32[0 5] right) {
                    stack i32[-2147483648 2147483647] saturated = left +| right;
                    stack i32[-2147483648 2147483647] wrapped = left +% right;

                    if (saturated > 15) {
                        return 1;
                    }

                    if (wrapped > 15) {
                        return 2;
                    }

                    return 3;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(3), resultValue.Value);
    }

    [Fact]
    public void FactDrivenBranchPruningKeepsWrappingArithmeticThatMayWrap()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i8[120 127] value) {
                    stack i8[-128 127] wrapped = value +% 1;
                    if (wrapped < 0) {
                        return 1;
                    }

                    return 2;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);
    }

    [Fact]
    public void FactDrivenBranchPruningUsesTextLiteralLengthFacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                fn i32[-2147483648 2147483647] Run() {
                    if (AsciiLength("stark") == 5) {
                        return 1;
                    }

                    return 2;
                }

                public finite law i64[0 9223372036854775807] AsciiLength(ascii source);
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(1), resultValue.Value);
    }

    [Fact]
    public void FactDrivenSwitchPruningRemovesCasesOutsideKnownInputRange()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[0 2] value) {
                    stack i32[-2147483648 2147483647] widened = (i32[-2147483648 2147483647])value;
                    switch (widened) {
                        case 0:
                            return 10;
                        case 1:
                            return 11;
                        case 2:
                            return 12;
                        case 10:
                            return 20;
                        case 11:
                            return 21;
                        default:
                            return 99;
                    }
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var switchCases = run.Blocks
            .Select(static block => block.Terminator.SwitchCases)
            .Where(static cases => cases is not null)
            .SelectMany(static cases => cases!)
            .ToArray();

        Assert.DoesNotContain(
            switchCases,
            static switchCase => switchCase.MatchValue is SsaIntegerConstant { Value: var value }
                                 && (value == new System.Numerics.BigInteger(10)
                                     || value == new System.Numerics.BigInteger(11)));
    }

    [Fact]
    public void FactDrivenSwitchPruningRewritesSingleReachableCaseToBranch()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[10 12] value) {
                    stack i32[-2147483648 2147483647] widened = (i32[-2147483648 2147483647])value;
                    switch (widened) {
                        case 10:
                            return 10;
                        case 40:
                            return 40;
                        case 41:
                            return 41;
                        default:
                            return 99;
                    }
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Switch);
        Assert.Contains(run.Blocks, static block => block.Terminator.Kind == SsaTerminatorKind.Branch);
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaBinaryRValue { Operator: SsaBinaryOperator.Equal });
    }

    [Fact]
    public void FactDrivenBranchPruningKeepsReusedForLoopVariableRangesScoped()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run() {
                    stack mut i32[0 200000] total = 0;
                    for willexit (stack mut i32[0 2] i = 0; i < 2; i += 1) {
                        total += 1;
                    }

                    for willexit (stack mut i32[0 100000] i = 0; i < 100000; i += 1) {
                        total += 1;
                    }

                    if (total == 100002) {
                        return 0;
                    }

                    return 1;
                }
                """),
            new CompilerOptions(StopAfterPassId: "prune-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var definitions = run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);

        Assert.Contains(
            run.Blocks,
            block => IsLessThanBranchAgainst(block, definitions, new System.Numerics.BigInteger(100000)));
        Assert.Contains(
            run.Blocks,
            static block => block.Terminator.Kind == SsaTerminatorKind.Return
                            && block.Terminator.Value is SsaIntegerConstant integer
                            && integer.Value.IsZero);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationRunsBeforeAbiLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    stack mut i32[-2147483648 2147483647] local = value;
                    local = local + 1;
                    return local;
                }
                """),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var executedPassIds = result.Executions
            .Where(static execution => execution.Status == PassExecutionStatus.Executed)
            .Select(static execution => execution.PassId)
            .ToArray();
        var memoryOptIndex = Array.IndexOf(executedPassIds, "memory-opt-ssa");
        var lowerAbiIndex = Array.IndexOf(executedPassIds, "lower-abi");
        var emitLlvmIndex = Array.IndexOf(executedPassIds, "emit-llvm");

        Assert.InRange(memoryOptIndex, 0, executedPassIds.Length - 1);
        Assert.True(memoryOptIndex < lowerAbiIndex, "Expected alias-aware memory optimization before ABI lowering.");
        Assert.True(memoryOptIndex < emitLlvmIndex, "Expected alias-aware memory optimization before LLVM emission.");
    }

    [Fact]
    public void AliasAwareMemoryOptimizationRemovesDeadForwardedStackScalarStores()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    stack mut i32[-2147483648 2147483647] local = value;
                    local = local + 1;
                    return local;
                }
                """),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(
            instructions.OfType<SsaStoreLocalInstruction>(),
            static store => store.LocalName == "local");
        Assert.DoesNotContain(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaLoadLocalRValue>(),
            static load => load.LocalName == "local");
    }

    [Fact]
    public void AliasAwareMemoryOptimizationKeepsAddressTakenStackScalarStoresConservative()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    stack mut i32[-2147483648 2147483647] local = value;
                    stack rawptr<i32[-2147483648 2147483647]> ptr = &local;
                    local = value + 1;
                    return *ptr;
                }
                """),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Equal(
            2,
            instructions.OfType<SsaStoreLocalInstruction>().Count(static store => store.LocalName == "local"));
        Assert.Contains(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaAddressOfLocalRValue>(),
            static address => address.LocalName == "local");
    }

    [Fact]
    public void AliasAwareMemoryOptimizationUsesFunctionEffectsForPureCallGlobalFacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                static mut i32[-2147483648 2147483647] Counter = 0;

                noinline finite law i32[-2147483648 2147483647] Touch(i32[-2147483648 2147483647] value) {
                    return value + 1;
                }

                fn i32[-2147483648 2147483647] Run(i32[-2147483648 2147483647] value) {
                    Counter = value;
                    stack i32[-2147483648 2147483647] first = Counter;
                    stack i32[-2147483648 2147483647] ignored = Touch(first);
                    return Counter;
                }
                """),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaLoadGlobalRValue>(),
            static load => load.GlobalName == "Counter");
        Assert.Contains(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaCallRValue>(),
            static call => call.FunctionName == "Touch");
    }

    private static bool HasIntegerRange(SsaValueFacts fact, int min, int max)
    {
        return fact.IntegerRangeKind == SsaFactLatticeKind.Known
               && fact.IntegerRange is { } range
               && range.Min == new System.Numerics.BigInteger(min)
               && range.Max == new System.Numerics.BigInteger(max);
    }

    private static bool HasLengthRange(SsaValueFacts fact, int min, int max)
    {
        return fact.LengthKind == SsaFactLatticeKind.Known
               && fact.LengthRange is { } range
               && range.Min == new System.Numerics.BigInteger(min)
               && range.Max == new System.Numerics.BigInteger(max);
    }

    private static bool IsLessThanBranchAgainst(
        SsaBasicBlock block,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        System.Numerics.BigInteger constant)
    {
        return block.Terminator is { Kind: SsaTerminatorKind.Branch, Condition: SsaValueReference condition }
               && definitions.TryGetValue(condition.Name, out var definition)
               && definition is SsaBinaryRValue { Operator: SsaBinaryOperator.LessThan, Right: var right }
               && TryResolveIntegerConstant(right, definitions, out var value)
               && value == constant;
    }

    private static bool TryResolveIntegerConstant(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions,
        out System.Numerics.BigInteger constant)
    {
        switch (value)
        {
            case SsaIntegerConstant integer:
                constant = integer.Value;
                return true;
            case SsaValueReference reference
                when definitions.TryGetValue(reference.Name, out var definition)
                     && definition is SsaUseRValue { Value: var referencedValue }:
                return TryResolveIntegerConstant(referencedValue, definitions, out constant);
            default:
                constant = default;
                return false;
        }
    }
}
