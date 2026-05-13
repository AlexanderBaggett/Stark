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

                noinline finite law i32[min max] Target() {
                    return 1;
                }

                unsafe fn i32[min max] Run() {
                    stack fnptr<fn i32[min max]()> op = Target;
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

                unsafe fn i32[min max] Run() {
                    stack fnptr<fn i32[min max](i32[min max])> increment =
                        (i32[min max] value) => value + 1;
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
        Assert.DoesNotContain(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            call => call.FunctionName == lambdaName);
        var returnValue = Assert.IsType<SsaIntegerConstant>(Assert.Single(run.Blocks).Terminator.Value);
        Assert.Equal(42, returnValue.Value);
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

                fn i32[min max] Run(i32[min max] value) {
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

                noinline finite law i32[min max] Target(i32[min max] value) {
                    return value + 1;
                }

                fn i32[min max] Run(bool flag, i32[min max] value) {
                    stack mut fnptr<fn i32[min max](i32[min max])> op = Target;
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

                finite law i32[min max] Target(i32[min max] value) {
                    return value + 1;
                }

                finite law i32[min max] Other(i32[min max] value) {
                    return value - 1;
                }

                fn i32[min max] Run(bool flag, i32[min max] value) {
                    stack mut fnptr<fn i32[min max](i32[min max])> op = Target;
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

                noinline finite law i32[min max] Target() {
                    return 1;
                }

                unsafe fn i32[min max] Run() {
                    stack fnptr<fn i32[min max]()> op = Target;
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

                fn i64[min max] Run(i64[min max] value) {
                    stack i64[min max] add = value + 0;
                    stack i64[min max] multiply = add * 1;
                    stack i64[min max] masked = multiply & -1;
                    stack i64[min max] shifted = masked << 0;
                    stack i64[min max] divided = shifted / 1;
                    stack i64[min max] rightShifted = divided >> 0;
                    return rightShifted ^ 0;
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
                or SsaBinaryOperator.Divide
                or SsaBinaryOperator.ShiftRight
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

                fn i64[min max] Run(i64[min max] value) {
                    stack i64[min max] sameAnd = value & value;
                    stack i64[min max] sameOr = sameAnd | sameAnd;
                    stack i64[min max] zeroXor = sameOr ^ sameOr;
                    stack i64[min max] zeroAnd = value & 0;
                    stack i64[min max] zeroMultiply = value * 0;
                    stack i64[min max] zeroSubtract = value - value;
                    stack i64[min max] zeroModulo = value % 1;
                    stack i64[min max] allOnes = value | -1;
                    return zeroXor + zeroAnd + zeroMultiply + zeroSubtract + zeroModulo + (allOnes & 1);
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
                or SsaBinaryOperator.Modulo
                or SsaBinaryOperator.Multiply
                or SsaBinaryOperator.BitwiseAnd
                or SsaBinaryOperator.BitwiseOr
                or SsaBinaryOperator.BitwiseXor);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(1), resultValue.Value);
    }

    [Fact]
    public void CleanupSsaForwardsAggregateFieldThroughMatchingBranchPhi()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Pair {
                    i32[min max] Value;
                    i32[min max] Tag;
                }

                fn i32[min max] Run(bool flag, i32[min max] value) {
                    stack Pair pair = flag
                        ? new Pair() { Value = value, Tag = 1 }
                        : new Pair() { Value = value, Tag = 2 };
                    return pair.Value;
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
                .OfType<SsaExtractFieldRValue>(),
            static extract => string.Equals(extract.FieldName, "Value", StringComparison.Ordinal));
    }

    [Fact]
    public void OptimizeSsaRemovesStaticRangeModuloAndDivisionIdentities()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[0 7] slot) {
                    stack u8[0 max] modulo = slot % (u8[0 max])8;
                    stack u8[0 max] divided = slot / (u8[0 max])8;
                    return modulo + divided;
                }
                """),
            new CompilerOptions(StopAfterPassId: "optimize-ssa"));

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
            static binary => binary.Operator is SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo);
    }

    [Fact]
    public void CleanupSsaRemovesSameOperandDivisionAndModuloWhenSourceRangeExcludesZero()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[1 100] value) {
                    stack u8[0 max] divided = value / value;
                    stack u8[0 max] modulo = value % value;
                    return divided + modulo;
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
            static binary => binary.Operator is SsaBinaryOperator.Divide or SsaBinaryOperator.Modulo);
    }

    [Fact]
    public void CleanupSsaRemovesSameOperandIntegerComparisons()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(i32[min max] value) {
                    stack i32[min max] equal = value == value ? 1 : 100;
                    stack i32[min max] notEqual = value != value ? 100 : 1;
                    stack i32[min max] less = value < value ? 100 : 1;
                    stack i32[min max] lessOrEqual = value <= value ? 1 : 100;
                    stack i32[min max] greater = value > value ? 100 : 1;
                    stack i32[min max] greaterOrEqual = value >= value ? 1 : 100;
                    return equal + notEqual + less + lessOrEqual + greater + greaterOrEqual;
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
            static binary => binary.Operator is SsaBinaryOperator.Equal
                or SsaBinaryOperator.NotEqual
                or SsaBinaryOperator.LessThan
                or SsaBinaryOperator.LessThanOrEqual
                or SsaBinaryOperator.GreaterThan
                or SsaBinaryOperator.GreaterThanOrEqual);
    }

    [Fact]
    public void ShapeBranchesSimplifiesBooleanReturnDiamondToCondition()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn bool Run(bool flag) {
                    if (flag) {
                        return true;
                    }

                    return false;
                }
                """),
            new CompilerOptions(StopAfterPassId: "shape-branches"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var block = Assert.Single(run.Blocks);
        var returnValue = Assert.IsType<SsaValueReference>(block.Terminator.Value);
        Assert.Equal("arg_flag", returnValue.Name);
        Assert.DoesNotContain(
            block.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaSelectRValue);
    }

    [Fact]
    public void InlineSsaInlinesSmallDirectCallsAndRerunsConstantPropagation()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                inline finite law i32[min max] AddOne(i32[min max] value) {
                    return value + 1;
                }

                unsafe fn i32[min max] Run() {
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

                finite law i32[min max] AddOne(i32[min max] value) {
                    return value + 1;
                }

                unsafe fn i32[min max] Run() {
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
    public void InlineSsaOptimizesThroughSourceBuiltDependencyBoundary()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-source-dependency-inline-ssa-");
        var mathPath = Path.Combine(tempDirectory.FullName, "Math.stark");
        var rootPath = Path.Combine(tempDirectory.FullName, "Demo.stark");

        try
        {
            File.WriteAllText(
                mathPath,
                """
                module Math

                public finite law i32[min max] AddOne(i32[min max] value) {
                    return value + 1;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    unsafe fn i32[min max] Run() {
                        return Math.AddOne(41);
                    }
                    """,
                    rootPath),
                new CompilerOptions(
                    StopAfterPassId: "inline-ssa",
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName)));

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
                static call => call.FunctionName.Contains("AddOne", StringComparison.Ordinal));

            var block = Assert.Single(run.Blocks);
            var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
            Assert.Equal(new System.Numerics.BigInteger(42), resultValue.Value);
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

                fn i32[min max] Run(i32[min max] value) {
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

            fn i32[min max] Run(i32[min max] value) {
                return value;
            }
            """);
        var generic = CompileRunFunction(
            """
            module Demo

            fn T Identity<T>(T value) {
                return value;
            }

            fn i32[min max] Run(i32[min max] value) {
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

                fn i32[min max] Run(i32[min max] value) {
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
    public void InlineSsaKeepsPublicOrdinaryNonWrapperDirectCallsWithoutExplicitInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                unsafe ffi fn void Touch();

                public fn i32[min max] AddOne(i32[min max] value) {
                    unsafe {
                        Touch();
                    }

                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value) {
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
    public void InlineSsaInlinesSmallPublicOrdinaryCallsWithConstantArguments()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public fn i32[min max] Double(i32[min max] value) {
                    return value * 2;
                }

                unsafe fn i32[min max] Run() {
                    return Double(21);
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
            static call => call.FunctionName == "Double");
        var returnValue = Assert.IsType<SsaIntegerConstant>(Assert.Single(run.Blocks).Terminator.Value);
        Assert.Equal(42, returnValue.Value);
    }

    [Fact]
    public void InlineSsaKeepsPublicOrdinaryConstantArgumentCallsWhenBodyHasDirectCalls()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                unsafe ffi fn void Touch();

                public fn i32[min max] AddOne(i32[min max] value) {
                    unsafe {
                        Touch();
                    }

                    return value + 1;
                }

                unsafe fn i32[min max] Run() {
                    return AddOne(41);
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
    public void InlineSsaInlinesPublicWrapperDirectCallsWithoutExplicitInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public fn i32[min max] AddOne(i32[min max] value) {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value) {
                    return AddOne(value);
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
        Assert.Contains(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaBinaryRValue>(),
            static binary => binary.Operator == SsaBinaryOperator.Add);
    }

    [Fact]
    public void InlineSsaInlinesSmallPublicLawDirectCallsWithoutExplicitInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                public finite law i32[min max] AddOne(i32[min max] value) {
                    return value + 1;
                }

                unsafe fn i32[min max] Run() {
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

                inline finite law i32[min max] AddOne(i32[min max] value) {
                    return value + 1;
                }

                inline finite law i32[min max] Forward(i32[min max] value) {
                    return AddOne(value);
                }

                unsafe fn i32[min max] Run() {
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

                noinline finite law i32[min max] AddOne(i32[min max] value) {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value) {
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

                inline finite law i32[min max] AddOne(i32[min max] value) {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value) {
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

                fn bool Run(u8[0 10] value) {
                    return value < 20;
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "value-facts",
                OptimizationLevel: CompilerOptimizationLevel.O0));

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
    public void ValueFactsEmitVerboseOptimizationTraceSummary()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn bool Run(u8[0 10] value) {
                    return value < 20;
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "value-facts",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var log = Assert.Single(
            result.Logs,
            static item => item.EventId == "ssa.value-facts.summary");

        Assert.Equal(CompilerLogKind.Decision, log.Kind);
        Assert.Equal(CompilerLogOutcome.Continued, log.Outcome);
        Assert.Equal(CompilerLogVerbosity.Verbose, log.Verbosity);
        Assert.Equal("value-facts", log.Stage);
        Assert.True(log.Data.ContainsKey("integerRanges"));
        Assert.True(log.Data.ContainsKey("knownBits"));
        Assert.True(log.Data.ContainsKey("booleans"));
    }

    [Fact]
    public void ValueFactsJoinIntegerRangesAtPhis()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn u8[0 10] Choose(bool flag, u8[2 3] left, u8[4 5] right) {
                    stack mut u8[0 10] result = left;
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

                fn i32[min max] AddAfterJoin(bool flag, u8[0 10] left, u8[20 30] right) {
                    stack mut i32[min max] value = left;
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

                fn u8[0 max] Combine(u8[0 15] value, u8[0 2] amount) {
                    stack u8[0 max] masked = (u8[0 max])(value & 7);
                    stack u8[0 max] shifted = (u8[0 max])(value << 2);
                    stack u8[0 max] restored = (u8[0 max])(shifted >> amount);
                    return (u8[0 max])(masked | restored);
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

                fn u8[0 max] Mask(u8[0 7] value) {
                    return (u8[0 max])(value & 8);
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
    public void ValueFactsUseConstantMasksToProveSignedValuesNonNegative()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Mask(i32[min max] value) {
                    return value & 255;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var maskFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Mask");
        var signBit = System.Numerics.BigInteger.One << 31;
        Assert.Contains(
            maskFacts.Values.Values,
            fact => fact.KnownBitsKind == SsaFactLatticeKind.Known
                    && fact.KnownBits is { } knownBits
                    && (knownBits.KnownZeroBits & signBit) != System.Numerics.BigInteger.Zero);
        Assert.Contains(maskFacts.Values.Values, static fact => HasIntegerRange(fact, 0, 255));
    }

    [Fact]
    public void ValueFactsUseKnownBitsToProveShiftedLowBitIsZero()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn u8[0 max] MaskShifted(u8[0 7] value) {
                    stack u8[0 max] shifted = (u8[0 max])(value << 1);
                    return (u8[0 max])(shifted & 1);
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

                fn bool ForcedBitCannotEqualZero(u8[0 7] value) {
                    stack u16[0 4095] forced = (u16[0 4095])(value | 8);
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
    public void ValueFactsPropagateDivisionAndModuloRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Divide(u8[0 100] value) {
                    return value / 10;
                }

                fn i32[min max] Modulo(u8[0 100] value) {
                    return value % 8;
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var divideFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Divide");
        Assert.Contains(divideFacts.Values.Values, static fact => HasIntegerRange(fact, 0, 10));

        var moduloFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Modulo");
        Assert.Contains(moduloFacts.Values.Values, static fact => HasIntegerRange(fact, 0, 7));
    }

    [Fact]
    public void FactDrivenBranchPruningUsesKnownBitsForMaskedComparisons()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[0 7] value) {
                    stack u8[0 max] masked = (u8[0 max])(value & 8);
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

                fn i32[min max] Run(u8[0 7] value) {
                    stack u16[0 4095] forced = (u16[0 4095])(value | 8);
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
    public void FactDrivenBranchPruningUsesModuloRangeFacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[0 100] value) {
                    stack i32[min max] slot = value % 8;
                    if (slot >= 8) {
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

                fn i32[min max] Run(u8[0 7] value) {
                    stack u8[0 max] shifted = (u8[0 max])(value << 1);
                    stack u8[0 max] masked = (u8[0 max])(shifted & 1);
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

                fn i32[min max] Run(u8[0 100] value) {
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
    public void ValueFactsCaptureBlockExitFactsForBranchScopedRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[0 100] value) {
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
        Assert.NotNull(runFacts.BlockExitValueFacts);
        Assert.True(runFacts.BlockExitValueFacts!.TryGetValue(trueTarget, out var trueExitFacts));
        Assert.Contains(trueExitFacts.Values, static valueFacts => HasIntegerRange(valueFacts, 0, 9));
    }

    [Fact]
    public void ValueFactsCaptureBranchTargetEntryNullability()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                unsafe fn i32[min max] Run(rawptr<i32[min max]> ptr) {
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

                unsafe fn i32[min max] Run(rawmutptr<i32[min max]> ptr) {
                    stack mut i32[min max] local = 1;
                    stack rawmutptr<i32[min max]> localPtr = &local;

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

                unsafe fn i32[min max] Run(rawmutptr<i32[min max]> ptr) {
                    stack mut i32[min max] local = 1;
                    stack rawmutptr<i32[min max]> localPtr = &local;

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

                unsafe fn i32[min max] Run() {
                    stack mut i32[min max] value = 1;
                    stack rawmutptr<i32[min max]> ptr = &value;
                    return *ptr;
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "cleanup-ssa",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var facts = new SsaValueFactAnalyzer().Analyze(ssa);
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

                fn i32[min max] Run(u8[0 2] index) {
                    stack i32[min max][3] values = { 4, 7, 9 };
                    stack i32[min max][] view = values;
                    return view[index];
                }
                """),
            new CompilerOptions(StopAfterPassId: "value-facts"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaValueFacts, out SsaValueFactModel? facts));
        Assert.NotNull(facts);

        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.Contains(
            runFacts.Values.Values,
            static fact => fact.Type.Kind == StarkTypeKind.Slice
                           && fact.LengthKind == SsaFactLatticeKind.Known
                           && fact.LengthRange is { Min: var min, Max: var max }
                           && min == new System.Numerics.BigInteger(3)
                           && max == new System.Numerics.BigInteger(3));
    }

    [Fact]
    public void ValueFactsJoinSliceLengthRangesAtPhis()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Read(bool flag, u8[0 1] index) {
                    stack i32[min max][3] left = { 1, 2, 3 };
                    stack i32[min max][5] right = { 4, 5, 6, 7, 8 };
                    stack mut i32[min max][] view = left;
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

                fn unicode Run(unicode text, u8[2 5] length) {
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

                fn u64[0 2 ** 63 - 1] Run() {
                    return AsciiLength("stark") + UnicodeLength((unicode)"llvm");
                }

                public finite law u64[0 2 ** 63 - 1] AsciiLength(ascii source);
                public finite law u64[0 2 ** 63 - 1] UnicodeLength(unicode source);
                """),
            new CompilerOptions(
                StopAfterPassId: "cleanup-ssa",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var facts = new SsaValueFactAnalyzer().Analyze(ssa);
        var runFacts = Assert.Single(facts.Functions.Values, static function => function.FunctionName == "Run");
        Assert.Contains(runFacts.Values.Values, static fact => HasIntegerRange(fact, 5, 5));
        Assert.Contains(runFacts.Values.Values, static fact => HasIntegerRange(fact, 4, 4));
    }

    [Fact]
    public void ConstantPropagationFoldsTextLiteralLengthCalls()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                fn u64[0 2 ** 63 - 1] Run() {
                    return AsciiLength("stark") + UnicodeLength((unicode)"llvm");
                }

                public finite law u64[0 2 ** 63 - 1] AsciiLength(ascii source);
                public finite law u64[0 2 ** 63 - 1] UnicodeLength(unicode source);
                """),
            new CompilerOptions(StopAfterPassId: "const-prop"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions),
            static instruction => instruction is SsaValueInstruction { Value: SsaCallRValue });

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(9), resultValue.Value);
    }

    [Fact]
    public void ValueFactsCaptureExplicitWrappingAndSaturatingArithmeticRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[0 10] left, u8[0 5] right) {
                    stack i32[min max] wrapped = left +% right;
                    stack i32[min max] saturated = left +| right;
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

                fn i32[min max] Run(u8[0 10] value) {
                    stack mut i32[min max] result = 0;
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

                fn i32[min max] Run(u8[0 100] value) {
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

                unsafe fn i32[min max] Run(rawptr<i32[min max]> ptr) {
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

                unsafe fn i32[min max] Run(rawmutptr<i32[min max]> ptr) {
                    stack mut i32[min max] local = 1;
                    stack rawmutptr<i32[min max]> localPtr = &local;

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

                fn i32[min max] Run(u8[0 15] value) {
                    stack u8[0 max] masked = (u8[0 max])(value & 7);
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

                fn i32[min max] Run(u8[0 10] left, u8[0 5] right) {
                    stack i32[min max] saturated = left +| right;
                    stack i32[min max] wrapped = left +% right;

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

                fn i32[min max] Run(i8[-1 max] value) {
                    stack i8[min max] wrapped = value +% 1;
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

                unsafe fn i32[min max] Run() {
                    if (AsciiLength("stark") == 5) {
                        return 1;
                    }

                    return 2;
                }

                public finite law u64[0 2 ** 63 - 1] AsciiLength(ascii source);
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
    public void AsciiToUnicodeLiteralSpecializationRewritesSmallLiteralCallsBeforeAbiLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe inline finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

                public unsafe fn bool Run() {
                    stack mut i32[min max][16] unicodeBuffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack mut Unicode ownedUnicode = new Unicode() {
                        Data = &unicodeBuffer[0],
                        Length = 0,
                        Capacity = 16
                    };

                    return TryConvertAsciiToUnicode(&ownedUnicode, "Stark");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-ascii-to-unicode-literals-ssa"));

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
            static call => call.FunctionName.Contains("TryConvertAsciiToUnicode", StringComparison.Ordinal));
        Assert.Contains(run.Blocks, static block => block.Label.Contains("ascii2unicode", StringComparison.Ordinal));
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreIndirectInstruction>(),
            static store => store.Value is SsaIntegerConstant integer && integer.Value == new System.Numerics.BigInteger(83));
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Phis),
            static phi => phi.Type.Kind == StarkTypeKind.Bool && phi.Incomings.Count == 3);
    }

    [Fact]
    public void AsciiToUnicodeLiteralSpecializationLowersLargeLiteralToSsaCopy()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe inline finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

                public unsafe fn bool Run() {
                    stack mut i32[min max][64] unicodeBuffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack mut Unicode ownedUnicode = new Unicode() {
                        Data = &unicodeBuffer[0],
                        Length = 0,
                        Capacity = 64
                    };

                    return TryConvertAsciiToUnicode(&ownedUnicode, "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789stark");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-ascii-to-unicode-literals-ssa"));

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
            static call => call.FunctionName.Contains("TryConvertAsciiToUnicode", StringComparison.Ordinal));
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaCopyMemoryInstruction>(),
            static copy => copy.SourceAddress is SsaTextDataAddressValue
                           && copy.CopyType is { Kind: StarkTypeKind.FixedArray, FixedLength: 41 });
    }

    [Fact]
    public void AsciiToUnicodeLiteralSpecializationHandlesLoopBackedgePhiSuccessors()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe inline finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

                public unsafe fn i32[min max] Run(i32[min max] count) {
                    stack mut i32[min max][16] unicodeBuffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack mut Unicode ownedUnicode = new Unicode() {
                        Data = &unicodeBuffer[0],
                        Length = 0,
                        Capacity = 16
                    };
                    stack mut i32[min max] index = 0;

                    while willexit (index < count) {
                        if (TryConvertAsciiToUnicode(&ownedUnicode, "Stark") == false) {
                            return -1;
                        }

                        index = index + 1;
                    }

                    return index;
                }
                """),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);

        Assert.DoesNotContain("call fastcc i1 @TryConvertAsciiToUnicode(", llvm.Text);
        Assert.DoesNotContain("; LLVM body emission fallback for Run", llvm.Text);
    }

    [Fact]
    public void AsciiToUnicodeLiteralSpecializationKeepsNonAsciiLiteralOnBuiltinPath()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe inline finite bool TryConvertAsciiToUnicode(rawmutptr<Unicode> destination, ascii source);

                public unsafe fn bool Run() {
                    stack mut i32[min max][16] unicodeBuffer = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
                    stack mut Unicode ownedUnicode = new Unicode() {
                        Data = &unicodeBuffer[0],
                        Length = 0,
                        Capacity = 16
                    };

                    return TryConvertAsciiToUnicode(&ownedUnicode, "caf\u00E9");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-ascii-to-unicode-literals-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaValueInstruction>()
                .Select(static instruction => instruction.Value)
                .OfType<SsaCallRValue>(),
            static call => call.FunctionName.Contains("TryConvertAsciiToUnicode", StringComparison.Ordinal));
    }

    [Fact]
    public void FactDrivenSwitchPruningRemovesCasesOutsideKnownInputRange()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[0 2] value) {
                    stack i32[min max] widened = (i32[min max])value;
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

                fn i32[min max] Run(u8[10 12] value) {
                    stack i32[min max] widened = (i32[min max])value;
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

                unsafe fn i32[min max] Run() {
                    stack mut u24[0 200000] total = 0;
                    for willexit (stack mut u8[0 2] i = 0; i < 2; i += 1) {
                        total += 1;
                    }

                    for willexit (stack mut u24[0 100000] i = 0; i < 100000; i += 1) {
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

                unsafe fn i32[min max] Run(i32[min max] value) {
                    stack mut i32[min max] local = value;
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

                unsafe fn i32[min max] Run(i32[min max] value) {
                    stack mut i32[min max] local = value;
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

                unsafe fn i32[min max] Run(i32[min max] value) {
                    stack mut i32[min max] local = value;
                    stack rawptr<i32[min max]> ptr = &local;
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
    public void AliasAwareMemoryOptimizationForwardsStackFieldLoadsFromSource()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Pair(i32[min max] Left, i32[min max] Right) { }

                fn i32[min max] Run(i32[min max] value) {
                    stack mut Pair pair = new Pair(0, 1);
                    pair.Left = value;
                    return pair.Left;
                }
                """),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaLoadIndirectRValue>(),
            static load => string.Equals(load.Text, "pair.Left", StringComparison.Ordinal));

        var returnValue = Assert.IsType<SsaValueReference>(Assert.Single(run.Blocks).Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsNestedStackFieldLoadsFromSource()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Inner(i32[min max] Value, i32[min max] Salt) { }
                record Outer(Inner Left, Inner Right) { }

                fn i32[min max] Run(i32[min max] value) {
                    stack mut Outer outer = new Outer(new Inner(0, 1), new Inner(2, 3));
                    outer.Left.Value = value;
                    return outer.Left.Value;
                }
                """),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaLoadIndirectRValue>(),
            static load => string.Equals(load.Text, "outer.Left.Value", StringComparison.Ordinal));

        var returnValue = Assert.IsType<SsaValueReference>(Assert.Single(run.Blocks).Terminator.Value);
        Assert.Equal("arg_value", returnValue.Name);
    }

    [Fact]
    public void AliasAwareMemoryOptimizationPreservesStackFieldFactsAcrossPureScalarCallsFromSource()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Pair(i32[min max] Left, i32[min max] Right) { }

                noinline finite law i32[min max] Touch(i32[min max] value) {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value) {
                    stack mut Pair pair = new Pair(0, 1);
                    pair.Left = value;
                    stack i32[min max] ignored = Touch(value);
                    return pair.Left + ignored;
                }
                """),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Contains(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaCallRValue>(),
            static call => call.FunctionName == "Touch");
        Assert.DoesNotContain(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaLoadIndirectRValue>(),
            static load => string.Equals(load.Text, "pair.Left", StringComparison.Ordinal));
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsStackFieldFactsAcrossSinglePredecessorBlocksFromSource()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Pair(i32[min max] Left, i32[min max] Right) { }

                fn i32[min max] Run(bool flag, i32[min max] value) {
                    stack mut Pair pair = new Pair(0, 1);
                    pair.Left = value;
                    if (flag) {
                        return pair.Left;
                    }

                    return pair.Left + 1;
                }
                """),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaLoadIndirectRValue>(),
            static load => string.Equals(load.Text, "pair.Left", StringComparison.Ordinal));
    }

    [Fact]
    public void AliasAwareMemoryOptimizationForwardsFixedArrayElementFactsFromSource()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn u8[0 200] Run(u8[0 100] value) {
                    stack mut u8[0 200][4] items = { 0, 0, 0, 0 };
                    items[0] = value;
                    items[1] = 7;
                    return items[0];
                }
                """),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.DoesNotContain(
            instructions.OfType<SsaValueInstruction>().Select(static instruction => instruction.Value).OfType<SsaLoadIndirectRValue>(),
            static load => string.Equals(load.Text, "items[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void ScalarReplacementRemovesDeadStackFieldStoresFromSource()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                record Pair(i32[min max] Left, i32[min max] Right) { }

                fn i32[min max] Run(i32[min max] value) {
                    stack mut Pair pair = new Pair(0, 1);
                    pair.Left = value;
                    return value + 1;
                }
                """),
            new CompilerOptions(StopAfterPassId: "sroa-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();

        Assert.Empty(instructions.OfType<SsaStoreIndirectInstruction>());
    }

    [Fact]
    public void AliasAwareMemoryOptimizationUsesFunctionEffectsForPureCallGlobalFacts()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                static mut i32[min max] Counter = 0;

                noinline finite law i32[min max] Touch(i32[min max] value) {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value) {
                    Counter = value;
                    stack i32[min max] first = Counter;
                    stack i32[min max] ignored = Touch(first);
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
