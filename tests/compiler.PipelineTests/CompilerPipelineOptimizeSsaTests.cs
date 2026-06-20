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

                noinline finite law i32[min max] Target()
                {
                    return 1;
                }

                unsafe fn i32[min max] Run()
                {
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

                unsafe fn i32[min max] Run()
                {
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
    public void InlineSsaPreservesSuccessorPhiIncomingFromSplitContinuationBlocks()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                enum Status
                {
                    Ok,
                    Err
                }

                inline fn bool IsOk(Status status)
                {
                    switch (status)
                    {
                        case Status.Ok: return true;
                        case Status.Err: return false;
                    }
                }

                noinline fn Status Op(u8[0 3] value)
                {
                    if (value == 3)
                    {
                        return Status.Err;
                    }

                    return Status.Ok;
                }

                unsafe fn i32[min max] Run(u8[0 3] value)
                {
                    if (!IsOk(Op(0)) || !IsOk(Op(1)) || !IsOk(Op(2)) || !IsOk(Op(value)))
                    {
                        return 3;
                    }

                    return 0;
                }
                """),
            new CompilerOptions(StopAfterPassId: "inline-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? optimizedSsa));
        Assert.NotNull(optimizedSsa);

        var run = Assert.Single(optimizedSsa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.Select(static block => block.Terminator),
            static terminator => terminator.Kind == SsaTerminatorKind.Return
                                 && terminator.Value is SsaIntegerConstant integer
                                 && integer.Value.IsZero);

        var joinPhi = Assert.Single(
            run.Blocks.SelectMany(static block => block.Phis),
            static phi => phi.VariableName.Contains("_or", StringComparison.Ordinal));
        Assert.Contains(
            joinPhi.Incomings,
            incoming => incoming.Value is SsaValueReference reference
                        && run.Blocks.Any(block => block.Id == incoming.PredecessorBlockId
                                                   && block.Label.Contains("continue", StringComparison.Ordinal)
                                                   && block.Instructions
                                                       .OfType<SsaValueInstruction>()
                                                       .Any(instruction => string.Equals(instruction.ResultName, reference.Name, StringComparison.Ordinal)
                                                                           && instruction.Value is SsaUnaryRValue { Operator: SsaUnaryOperator.LogicalNot })));
    }

    [Fact]
    public void OptimizeSsaPreservesMaterializedGenericCallSymbols()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn T Identity<T>(T value)
                {
                    return value;
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

                noinline finite law i32[min max] Target(i32[min max] value)
                {
                    return value + 1;
                }

                fn i32[min max] Run(bool flag, i32[min max] value)
                {
                    stack mut fnptr<fn i32[min max](i32[min max])> op = Target;
                    if (flag)
                    {
                        op = Target;
                    }
                    else
                    {
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

                finite law i32[min max] Target(i32[min max] value)
                {
                    return value + 1;
                }

                finite law i32[min max] Other(i32[min max] value)
                {
                    return value - 1;
                }

                fn i32[min max] Run(bool flag, i32[min max] value)
                {
                    stack mut fnptr<fn i32[min max](i32[min max])> op = Target;
                    if (flag)
                    {
                        op = Target;
                    }
                    else
                    {
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
    public void CleanupSsaRemovesSourceLevelIntegerAlgebraicIdentities()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i64[min max] Run(i64[min max] value)
                {
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

                fn i64[min max] Run(i64[min max] value)
                {
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
    public void ArithmeticFoldSsaFoldsRepeatedUnknownAddsToMultiply()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value + value + value;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(
            binaries,
            static binary => binary.Operator == SsaBinaryOperator.Multiply
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(3));
        Assert.DoesNotContain(binaries, static binary => binary.Operator == SsaBinaryOperator.Add);
    }

    [Fact]
    public void ArithmeticFoldSsaFoldsLongRepeatedAddRunToSingleMultiply()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value + value + value + value + value + value;
            }
            """);

        var multiply = Assert.Single(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.Multiply);
        var coefficient = Assert.IsType<SsaIntegerConstant>(multiply.Right);
        Assert.Equal(new System.Numerics.BigInteger(6), coefficient.Value);
    }

    [Fact]
    public void ArithmeticFoldSsaPreservesTrailingConstantsAfterRepeatedAddRun()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value + value + value + 5;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(
            binaries,
            static binary => binary.Operator == SsaBinaryOperator.Multiply
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(3));
        Assert.Contains(
            binaries,
            static binary => binary.Operator == SsaBinaryOperator.Add
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(5));
    }

    [Fact]
    public void ArithmeticFoldSsaFoldsMultipleContiguousRepeatedTerms()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] x, i32[min max] y)
            {
                return x + x + y + y + y + 4;
            }
            """);

        var coefficients = GetBinaryRValues(run)
            .Where(static binary => binary.Operator == SsaBinaryOperator.Multiply)
            .Select(static binary => Assert.IsType<SsaIntegerConstant>(binary.Right).Value)
            .Order()
            .ToArray();

        Assert.Equal([new System.Numerics.BigInteger(2), new System.Numerics.BigInteger(3)], coefficients);
    }

    [Fact]
    public void ArithmeticFoldSsaCombinesSafeConstantCoefficientTerms()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i16[min max] Run(i16[-1000 1000] x)
            {
                return (x * 2) + (x * 3) - x;
            }
            """);

        var multiply = Assert.Single(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.Multiply);
        var coefficient = Assert.IsType<SsaIntegerConstant>(multiply.Right);
        Assert.Equal(new System.Numerics.BigInteger(4), coefficient.Value);
    }

    [Fact]
    public void ArithmeticFoldSsaDoesNotCombineUnsafeConstantCoefficientTerms()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] x)
            {
                return (x * 2) + (x * 3) - x;
            }
            """);

        Assert.DoesNotContain(
            GetBinaryRValues(run),
            static binary => binary.Operator == SsaBinaryOperator.Multiply
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(4));
    }

    [Fact]
    public void ArithmeticFoldSsaRecognizesSafeLeftShiftCoefficients()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn u16[min max] Run(u16[0 1000] x)
            {
                return (x << 1) + x + x;
            }
            """);

        var multiply = Assert.Single(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.Multiply);
        var coefficient = Assert.IsType<SsaIntegerConstant>(multiply.Right);
        Assert.Equal(new System.Numerics.BigInteger(4), coefficient.Value);
    }

    [Fact]
    public void ArithmeticFoldSsaDoesNotTreatUnsafeLeftShiftAsCoefficient()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] x)
            {
                return (x << 1) + x + x;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(binaries, static binary => binary.Operator == SsaBinaryOperator.ShiftLeft);
        Assert.DoesNotContain(
            binaries,
            static binary => binary.Operator == SsaBinaryOperator.Multiply
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(4));
    }

    [Fact]
    public void ArithmeticFoldSsaFoldsRepeatedUnaryNegatedTerms()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i16[min max] Run(i16[-1000 1000] x)
            {
                return -x + -x;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(
            binaries,
            static binary => binary.Operator == SsaBinaryOperator.Multiply
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(2));
        Assert.Contains(binaries, static binary => binary.Operator == SsaBinaryOperator.Subtract);
    }

    [Fact]
    public void ArithmeticFoldSsaCombinesSameSignConstantTail()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value + 5 + 6;
            }
            """);

        var add = Assert.Single(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.Add);
        var constant = Assert.IsType<SsaIntegerConstant>(add.Right);
        Assert.Equal(new System.Numerics.BigInteger(11), constant.Value);
    }

    [Fact]
    public void ArithmeticFoldSsaDropsAdjacentZeroCoefficientTerms()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value - value;
            }
            """);

        var block = Assert.Single(run.Blocks);
        var resultValue = Assert.IsType<SsaIntegerConstant>(block.Terminator.Value);
        Assert.Equal(new System.Numerics.BigInteger(0), resultValue.Value);
        Assert.Empty(GetBinaryRValues(run));
    }

    [Fact]
    public void ArithmeticFoldSsaFoldsSafeRepeatedSubtractionRuns()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i16[min max] Run(i16[-8000 8000] x, i16[-3000 3000] y)
            {
                return x + x + x - y - y + 4;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(
            binaries,
            static binary => binary.Operator == SsaBinaryOperator.Multiply
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(3));
        Assert.Contains(
            binaries,
            static binary => binary.Operator == SsaBinaryOperator.Multiply
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(2));
        Assert.Contains(binaries, static binary => binary.Operator == SsaBinaryOperator.Subtract);
    }

    [Fact]
    public void ArithmeticFoldSsaDoesNotFoldUnsafeRepeatedSubtractionRuns()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] x, i32[min max] y)
            {
                return x - y - y;
            }
            """);

        Assert.DoesNotContain(
            GetBinaryRValues(run),
            static binary => binary.Operator == SsaBinaryOperator.Multiply);
    }

    [Fact]
    public void ArithmeticFoldSsaDoesNotReassociateSeparatedOrdinaryTerms()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] x, i32[min max] y)
            {
                return x + y + x;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(binaries, static binary => binary.Operator == SsaBinaryOperator.Add);
        Assert.DoesNotContain(binaries, static binary => binary.Operator == SsaBinaryOperator.Multiply);
    }

    [Fact]
    public void ArithmeticFoldSsaFoldsWrappingAddRunsWithWrappingMultiply()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i8[min max] Run(i8[min max] value)
            {
                return value +% value +% value;
            }
            """);

        var multiply = Assert.Single(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.WrappingMultiply);
        var coefficient = Assert.IsType<SsaIntegerConstant>(multiply.Right);
        Assert.Equal(new System.Numerics.BigInteger(3), coefficient.Value);
    }

    [Fact]
    public void ArithmeticFoldSsaFoldsWrappingSubtractRunsWithWrappingMultiply()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i8[min max] Run(i8[min max] x, i8[min max] y)
            {
                return x +% x -% y -% y;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(
            binaries,
            static binary => binary.Operator == SsaBinaryOperator.WrappingMultiply
                             && binary.Right is SsaIntegerConstant { Value: var value }
                             && value == new System.Numerics.BigInteger(2));
        Assert.Contains(binaries, static binary => binary.Operator == SsaBinaryOperator.WrappingSubtract);
    }

    [Fact]
    public void ArithmeticFoldSsaDoesNotRewriteSaturatingAddChains()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value +| value +| value;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(binaries, static binary => binary.Operator == SsaBinaryOperator.SaturatingAdd);
        Assert.DoesNotContain(binaries, static binary => binary.Operator == SsaBinaryOperator.Multiply);
    }

    [Fact]
    public void ArithmeticFoldSsaDoesNotRewriteSaturatingSubtractChains()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] x, i32[min max] y)
            {
                return x -| y -| y;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(binaries, static binary => binary.Operator == SsaBinaryOperator.SaturatingSubtract);
        Assert.DoesNotContain(binaries, static binary => binary.Operator == SsaBinaryOperator.Multiply);
    }

    [Fact]
    public void ArithmeticFoldSsaOptimizesImportedGenericTypedTemplateAfterSourceBodyRemoval()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-arithmetic-fold-ssa-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");
        var libraryPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "Facade.lib" : "libFacade.a");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public fn i32[min max] Scale<T>(T tag, i32[min max] value)
                {
                    return value + value + value;
                }
                """,
                facadePath));

            Assert.True(libraryResult.Succeeded, string.Join(", ", libraryResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

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

            File.WriteAllText(manifestPath, typedOnlyManifest.ToJson());
            File.Delete(facadePath);

            var consumerResult = pipeline.Run(
                new CompilationInput(
                    """
                    import Facade
                    module Demo

                    fn i32[min max] Run(i32[min max] value)
                    {
                        return Facade.Scale(true, value);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "arithmetic-fold-ssa"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
            Assert.NotNull(ssa);

            var scale = Assert.Single(ssa.Functions, static function => function.Name.Contains("Facade_Scale", StringComparison.Ordinal));
            Assert.Contains(
                GetBinaryRValues(scale),
                static binary => binary.Operator == SsaBinaryOperator.Multiply
                                 && binary.Right is SsaIntegerConstant { Value: var value }
                                 && value == new System.Numerics.BigInteger(3));
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
    public void ArithmeticFoldSsaFoldsRepeatedMultiplicationRunToExponent()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value * value * value * value;
            }
            """);

        var exponent = Assert.Single(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.Exponent);
        var exponentValue = Assert.IsType<SsaIntegerConstant>(exponent.Right);
        Assert.Equal(new System.Numerics.BigInteger(4), exponentValue.Value);
        Assert.DoesNotContain(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.Multiply);
    }

    [Fact]
    public void ArithmeticFoldSsaGroupsIndependentRepeatedProductFactors()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] x, i32[min max] y)
            {
                return x * x * y * y * y;
            }
            """);

        var exponents = GetBinaryRValues(run)
            .Where(static binary => binary.Operator == SsaBinaryOperator.Exponent)
            .Select(static binary => Assert.IsType<SsaIntegerConstant>(binary.Right).Value)
            .Order()
            .ToArray();

        Assert.Equal([new System.Numerics.BigInteger(2), new System.Numerics.BigInteger(3)], exponents);
        Assert.Contains(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.Multiply);
    }

    [Fact]
    public void ArithmeticFoldSsaKeepsSeparatedProductFactorsSourceShaped()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] x, i32[min max] y)
            {
                return x * y * x;
            }
            """);

        Assert.DoesNotContain(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.Exponent);
    }

    [Fact]
    public void ArithmeticFoldSsaFoldsWrappingProductRunsToExponent()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i8[min max] Run(i8[min max] value)
            {
                return value *% value *% value;
            }
            """);

        var exponent = Assert.Single(GetBinaryRValues(run), static binary => binary.Operator == SsaBinaryOperator.WrappingExponent);
        var exponentValue = Assert.IsType<SsaIntegerConstant>(exponent.Right);
        Assert.Equal(new System.Numerics.BigInteger(3), exponentValue.Value);
    }

    [Fact]
    public void ArithmeticFoldSsaDoesNotRewriteSaturatingProductChains()
    {
        var run = CompileRunAfterArithmeticFold(
            """
            module Demo

            fn i32[min max] Run(i32[min max] value)
            {
                return value *| value *| value;
            }
            """);

        var binaries = GetBinaryRValues(run);
        Assert.Contains(binaries, static binary => binary.Operator == SsaBinaryOperator.SaturatingMultiply);
        Assert.DoesNotContain(binaries, static binary => binary.Operator == SsaBinaryOperator.Exponent);
    }

    [Fact]
    public void ArithmeticFoldSsaRenderShowsFoldedLinearAndProductShapes()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Scale(i32[min max] value)
                {
                    return value + value + value;
                }

                fn i32[min max] Power(i32[min max] value)
                {
                    return value * value * value;
                }
                """),
            new CompilerOptions(StopAfterPassId: "arithmetic-fold-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var rendered = ArtifactTextRenderer.Render(ssa);
        Assert.Contains("* 3", rendered, StringComparison.Ordinal);
        Assert.Contains("** 3", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupSsaForwardsAggregateFieldThroughMatchingBranchPhi()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Pair
                {
                    i32[min max] Value;
                    i32[min max] Tag;
                }

                fn i32[min max] Run(bool flag, i32[min max] value)
                {
                    stack Pair pair = flag
                        ? new Pair()
                        {
                            Value = value, Tag = 1
                        }
                        : new Pair()
                        {
                            Value = value, Tag = 2
                        };
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

                fn i32[min max] Run(u8[0 7] slot)
                {
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

                fn i32[min max] Run(u8[1 100] value)
                {
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

                fn i32[min max] Run(i32[min max] value)
                {
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

                fn bool Run(bool flag)
                {
                    if (flag)
                    {
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

                inline finite law i32[min max] AddOne(i32[min max] value)
                {
                    return value + 1;
                }

                unsafe fn i32[min max] Run()
                {
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
    public void AggregateConstructionSsaStoresFullConstructorsDirectlyIntoNonEscapedLocalFields()
    {
        const string source = """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn i32[min max] Run()
            {
                heap mut Pair value = new Pair()
                {
                    Left = 1, Right = 2
                };
                return value.Left;
            }
            """;

        var pipeline = DefaultCompilerPipeline.Create();
        var before = pipeline.Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));
        Assert.True(before.Succeeded, string.Join(", ", before.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(before.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? beforeSsa));
        Assert.NotNull(beforeSsa);
        var beforeRun = Assert.Single(beforeSsa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            beforeRun.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreLocalInstruction>(),
            static store => store.LocalName == "value" && store.LocalType.NamedType == "Pair");

        var after = pipeline.Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "aggregate-construction-ssa"));
        Assert.True(after.Succeeded, string.Join(", ", after.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(after.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? afterSsa));
        Assert.NotNull(afterSsa);
        var afterRun = Assert.Single(afterSsa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            afterRun.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreLocalInstruction>(),
            static store => store.LocalName == "value" && store.LocalType.NamedType == "Pair");
        Assert.True(
            afterRun.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaStoreIndirectInstruction>()
                .Count(static store => store.ValueType.Kind == StarkTypeKind.Integer) >= 2);

        var full = pipeline.Run(new CompilationInput(source));
        Assert.True(full.Succeeded, string.Join(", ", full.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void AggregateConstructionSsaKeepsWholeStoreForRawEscapedLocal()
    {
        const string source = """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            unsafe ffi fn void Observe(rawptr<Pair> pointer);

            unsafe fn i32[min max] Run()
            {
                heap mut Pair value = new Pair()
                {
                    Left = 1, Right = 2
                };
                stack rawptr<Pair> pointer = &value;
                Observe(pointer);
                return value.Left;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "aggregate-construction-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreLocalInstruction>(),
            static store => store.LocalName == "value" && store.LocalType.NamedType == "Pair");
    }

    [Fact]
    public void OwnershipTrafficSsaElidesDeadAggregateMoveTrafficForNonEscapedRoots()
    {
        const string source = """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            fn void Run()
            {
                heap mut Pair source = new Pair()
                {
                    Left = 1, Right = 2
                };
                heap mut Pair destination = new Pair()
                {
                    Left = 3, Right = 4
                };
                destination = source;
                return;
            }
            """;

        var pipeline = DefaultCompilerPipeline.Create();
        var before = pipeline.Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "memory-opt-ssa"));
        Assert.True(before.Succeeded, string.Join(", ", before.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(before.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? beforeSsa));
        Assert.NotNull(beforeSsa);
        var beforeRun = Assert.Single(beforeSsa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            beforeRun.Blocks.SelectMany(static block => block.Instructions).OfType<SsaCopyMemoryInstruction>(),
            static copy => copy.TransferKind == SsaMemoryTransferKind.Move);
        Assert.Contains(
            beforeRun.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreLocalInstruction>(),
            static store => store.LocalName == "source" && store.Value is SsaUndefValue);

        var after = pipeline.Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "ownership-traffic-ssa"));
        Assert.True(after.Succeeded, string.Join(", ", after.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(after.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? afterSsa));
        Assert.NotNull(afterSsa);
        var afterRun = Assert.Single(afterSsa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            afterRun.Blocks.SelectMany(static block => block.Instructions).OfType<SsaCopyMemoryInstruction>(),
            static copy => copy.TransferKind == SsaMemoryTransferKind.Move);
        Assert.DoesNotContain(
            afterRun.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreLocalInstruction>(),
            static store => store.LocalName == "source" && store.Value is SsaUndefValue);
    }

    [Fact]
    public void OwnershipTrafficSsaKeepsMoveInvalidationForRawEscapedRoots()
    {
        const string source = """
            module Demo

            struct Pair
            {
                i32[min max] Left;
                i32[min max] Right;
            }

            unsafe ffi fn void Observe(rawptr<Pair> pointer);

            fn void Consume(Pair value)
            {
                return;
            }

            unsafe fn void Run()
            {
                heap mut Pair source = new Pair()
                {
                    Left = 1, Right = 2
                };
                stack rawptr<Pair> pointer = &source;
                Consume(source);
                Observe(pointer);
                return;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "ownership-traffic-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreLocalInstruction>(),
            static store => store.LocalName == "source" && store.Value is SsaUndefValue);
    }

    [Fact]
    public void InlineSsaPreservesOwnershipSummariesOnRewrittenFunctions()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                struct Box
                {
                    i32[min max] Value;
                }

                inline finite law i32[min max] AddOne(i32[min max] value)
                {
                    return value + 1;
                }

                fn void Consume(Box value)
                {
                    return;
                }

                unsafe fn i32[min max] Run()
                {
                    stack mut Box box = new Box()
                    {
                        Value = AddOne(0)
                    };
                    stack rawptr<Box> pointer = &box;
                    Consume(box);
                    box = new Box()
                    {
                        Value = AddOne(1)
                    };
                    return box.Value;
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

        Assert.NotNull(run.Ownership);
        Assert.Contains(run.Ownership!.Events, static ev => ev.Kind == OwnershipEventKind.AddressTaken && ev.Place.RootName == "box");
        Assert.Contains(run.Ownership.Events, static ev => ev.Kind == OwnershipEventKind.Move && ev.Place.RootName == "box");
        Assert.Contains(run.Ownership.Events, static ev => ev.Kind == OwnershipEventKind.Reinitialize && ev.Place.RootName == "box");
        Assert.Contains(run.Ownership.Roots, static root => root.Name == "box" && root.HasRawPointerEscape && root.HasMove);
    }

    [Fact]
    public void DynamicStorageSsaElidesReserveWhenCapacityFactsProveNoop()
    {
        const string source = """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(8);
                values.Reserve(4);
                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
    }

    [Fact]
    public void DynamicStorageSsaFoldsTryReserveWhenCapacityFactsProveSuccess()
    {
        const string source = """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(8);
                if (!values.TryReserve(4))
                {
                    return 1;
                }

                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageTryReserveRValue);
        Assert.DoesNotContain(
            run.Blocks.Select(static block => block.Terminator),
            static terminator => terminator.Value is SsaIntegerConstant integer
                                 && integer.Value == System.Numerics.BigInteger.One);
    }

    [Fact]
    public void DynamicStorageSsaKeepsTryReserveWhenCapacityMayBeInsufficient()
    {
        const string source = """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(4);
                if (!values.TryReserve(8))
                {
                    return 1;
                }

                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageTryReserveRValue);
    }

    [Fact]
    public void DynamicStorageSsaKeepsReserveAfterOpaqueDynamicOwnerCall()
    {
        const string source = """
            module Demo

            unsafe ffi fn void Observe(mut borrow dynamic u32[0 2 ** 31 - 1] values);

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 2 ** 31 - 1] values = new(8);
                Observe(values);
                values.Reserve(4);
                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
    }

    [Fact]
    public void DynamicStorageSsaPreservesCapacityFactsAcrossFullInitSliceHelperCall()
    {
        const string source = """
            module System.Memory

            public enum MemoryError
            {
                OutOfMemory,
            }

            public enum MemoryStatus
            {
                Ok,
                Err(MemoryError),
            }

            public inline finite MemoryStatus FillBytes(
                init i8[min max][] destination,
                i8[min max] value,
                u64[0 2 ** 63 - 1] count);

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic i8[min max] values = new(8);
                stack init i8[min max][] tail = init values[values.Length, 4];
                FillBytes(tail, 7, 4);
                values.Reserve(2);
                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
    }

    [Fact]
    public void DynamicStorageSsaDoesNotCommitLengthAfterUnrecognizedInitSliceCall()
    {
        const string source = """
            module System.Memory

            public enum MemoryError
            {
                OutOfMemory,
            }

            public enum MemoryStatus
            {
                Ok,
                Err(MemoryError),
            }

            public fn MemoryStatus UnknownFill(
                init i8[min max][] destination,
                i8[min max] value,
                u64[0 2 ** 63 - 1] count);

            unsafe fn i8[min max] Run()
            {
                stack mut dynamic i8[min max] values = new(8);
                stack init i8[min max][] tail = init values[values.Length, 4];
                UnknownFill(tail, 7, 4);
                return values.MoveLast();
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageMoveLastRValue { IsKnownNonEmpty: false });
    }

    [Fact]
    public void DynamicStorageSsaPreservesCapacityFactsAcrossReadOnlyLawCall()
    {
        const string source = """
            module Demo

            noinline finite law u64[0 2 ** 63 - 1] Observe(borrow dynamic u32[0 max] values)
            {
                return values.Capacity;
            }

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 max] values = new(8);
                if (Observe(values) == 0)
                {
                    return 0;
                }

                values.Reserve(4);
                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
    }

    [Fact]
    public void DynamicStorageSsaPreservesCapacityFactsAcrossImportedReadOnlyLawCall()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-dynamic-readonly-call-ssa-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public noinline finite law u64[0 2 ** 63 - 1] Observe(borrow dynamic u32[0 max] values)
                {
                    return values.Capacity;
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

                    unsafe fn u64[0 2 ** 63 - 1] Run()
                    {
                        stack mut dynamic u32[0 max] values = new(8);
                        if (Facade.Observe(values) == 0)
                        {
                            return 0;
                        }

                        values.Reserve(4);
                        return values.Capacity;
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "dynamic-storage-ssa"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
            Assert.NotNull(ssa);

            var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
            Assert.DoesNotContain(
                run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
                static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
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
    public void DynamicStorageSsaPreservesFactsAcrossImportedGenericInlineHelper()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-dynamic-generic-inline-ssa-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public inline fn void AppendTagged<T>(
                    mut borrow dynamic u32[0 max] values,
                    u32[0 max] value,
                    T tag)
                    {
                        init values[values.Length] = value;
                    return;
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

                    unsafe fn u32[0 max] Run()
                    {
                        stack mut dynamic u32[0 max] values = new(4);
                        Facade.AppendTagged(values, (u32[0 max])10, (u8[0 max])1);
                        values.Reserve(2);
                        return values.MoveLast();
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "dynamic-storage-ssa"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
            Assert.NotNull(ssa);

            var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
            var instructions = run.Blocks.SelectMany(static block => block.Instructions).ToArray();
            Assert.DoesNotContain(
                instructions.OfType<SsaCallInstruction>(),
                static call => call.FunctionName.Contains("AppendTagged", StringComparison.Ordinal));
            Assert.DoesNotContain(
                instructions.OfType<SsaValueInstruction>(),
                static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
            Assert.Contains(
                instructions.OfType<SsaValueInstruction>(),
                static instruction => instruction.Value is SsaDynamicStorageMoveLastRValue { IsKnownNonEmpty: true });
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
    public void DynamicStorageSsaElidesFreeWhenBackingAllocationProvenAbsent()
    {
        const string source = """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 max] values = new(0);
                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageFreeRValue);
    }

    [Fact]
    public void DynamicStorageSsaKeepsFreeWhenBackingAllocationMayExist()
    {
        const string source = """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 max] values = new(1);
                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageFreeRValue);
    }

    [Fact]
    public void DynamicStorageSsaMarksMoveLastNonEmptyWhenPrefixFactsProveLength()
    {
        const string source = """
            module Demo

            unsafe fn u32[0 max] Run()
            {
                stack mut dynamic u32[0 max] values = new(2);
                init values[0] = 10;
                return values.MoveLast();
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageMoveLastRValue { IsKnownNonEmpty: true });
    }

    [Fact]
    public void DynamicStorageSsaMarksMoveAtInBoundsWhenIndexAndPrefixFactsProveIt()
    {
        const string source = """
            module Demo

            unsafe fn u32[0 max] Run()
            {
                stack mut dynamic u32[0 max] values = new(2);
                init values[0] = 10;
                init values[1] = 20;
                return values.MoveAt(0);
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageMoveAtRValue { IsKnownInBounds: true });
    }

    [Fact]
    public void DynamicStorageSsaKeepsMoveAtBoundsCheckWhenIndexMayReachLength()
    {
        const string source = """
            module Demo

            unsafe fn u32[0 max] Run(u8[0 2] index)
            {
                stack mut dynamic u32[0 max] values = new(2);
                init values[0] = 10;
                init values[1] = 20;
                return values.MoveAt(index);
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageMoveAtRValue { IsKnownInBounds: false });
    }

    [Fact]
    public void DynamicStorageSsaDoesNotPromoteSparseUnsafeReadsToDensePrefixFacts()
    {
        const string source = """
            module Demo

            fn u32[0 max] Run(u8[0 1] index)
            {
                stack mut dynamic u32[0 max] values = new(2);
                unsafe
                {
                    stack u32[0 max] observed = values[index];
                }

                return values.MoveLast();
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageMoveLastRValue { IsKnownNonEmpty: false });
    }

    [Fact]
    public void DynamicStorageSsaKeepsDistinctFieldOwnerFactsSeparate()
    {
        const string source = """
            module Demo

            struct Pair
            {
                dynamic u32[0 max] Left;
                dynamic u32[0 max] Right;
            }

            unsafe fn u32[0 max] Run()
            {
                stack mut Pair pair = new Pair()
                {
                    Left = new(2),
                    Right = new(2)
                };

                init pair.Left[0] = 10;
                return pair.Right.MoveLast();
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageMoveLastRValue { IsKnownNonEmpty: false });
    }

    [Fact]
    public void DynamicStorageSsaPreservesEmptyPrefixAfterClearLoop()
    {
        const string source = """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run()
            {
                stack mut dynamic u32[0 max] values = new(4);
                init values[0] = 10;
                init values[1] = 20;

                while willexit (values.Length > 0)
                {
                    stack u32[0 max] discarded = values.MoveLast();
                }

                values.Reserve(4);
                return values.Capacity;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-storage-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.DoesNotContain(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaDynamicStorageReserveRValue);
    }

    [Fact]
    public void DynamicAppendLoopSsaCommitsLengthOnceAfterLoop()
    {
        const string source = """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run(i8[min max] value, u8[0 10] count)
            {
                stack mut dynamic i8[min max] values = new(count);
                if (!values.TryReserve(count))
                {
                    return 0;
                }

                for willexit (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    init values[values.Length] = value;
                }

                return values.Length;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-append-loop-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var definitions = CollectSsaDefinitions(run);
        var body = Assert.Single(run.Blocks, static block => block.Label.Contains("for_body", StringComparison.Ordinal));
        var exit = Assert.Single(run.Blocks, static block => block.Label.Contains("for_exit", StringComparison.Ordinal));

        Assert.DoesNotContain(
            body.Instructions.OfType<SsaStoreIndirectInstruction>(),
            store => IsDynamicLengthAddress(store.Address, definitions));
        Assert.Contains(
            body.Instructions.OfType<SsaValueInstruction>(),
            static instruction => instruction.Value is SsaElementAddressRValue
            {
                Address: SsaValueReference { Name: var addressName },
                Index: SsaValueReference { Name: var indexName }
            } && addressName.StartsWith("dynamic_append_tail", StringComparison.Ordinal)
              && indexName.EndsWith("_phi", StringComparison.Ordinal));
        Assert.Contains(
            exit.Instructions.OfType<SsaStoreIndirectInstruction>(),
            store => IsDynamicLengthAddress(store.Address, definitions));
    }

    [Fact]
    public void DynamicAppendLoopSsaKeepsPerIterationCommitWhenValueReadsChangingLength()
    {
        const string source = """
            module Demo

            unsafe fn u64[0 2 ** 63 - 1] Run(u8[0 10] count)
            {
                stack mut dynamic u64[0 max] values = new(count);
                if (!values.TryReserve(count))
                {
                    return 0;
                }

                for willexit (stack mut u8[0 10] index = 0; index < count; index += 1)
                {
                    init values[values.Length] = values.Length;
                }

                return values.Length;
            }
            """;

        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "dynamic-append-loop-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var definitions = CollectSsaDefinitions(run);
        var body = Assert.Single(run.Blocks, static block => block.Label.Contains("for_body", StringComparison.Ordinal));
        var exit = Assert.Single(run.Blocks, static block => block.Label.Contains("for_exit", StringComparison.Ordinal));

        Assert.Contains(
            body.Instructions.OfType<SsaStoreIndirectInstruction>(),
            store => IsDynamicLengthAddress(store.Address, definitions));
        Assert.DoesNotContain(
            exit.Instructions.OfType<SsaStoreIndirectInstruction>(),
            store => IsDynamicLengthAddress(store.Address, definitions));
    }

    [Fact]
    public void InlineSsaPreservesConstProvenanceOnSynthesizedArgumentSlots()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                inline unsafe finite law i32[min max] Read(const i32[min max] value)
                {
                    return *(&value);
                }

                unsafe fn i32[min max] Run(bool flag, const i32[min max] left, const i32[min max] right)
                {
                    return Read(flag ? left : right);
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
            static call => call.FunctionName == "Read");

        var slot = Assert.Single(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaAllocateLocalInstruction>(),
            static instruction => instruction.LocalName.StartsWith("arg_value_slot_inl", StringComparison.Ordinal));

        Assert.True(slot.IsImmutable);
        Assert.True(slot.HasConstProvenance);
        Assert.Equal(ConstProvenanceKind.PermanentConst, slot.ConstProvenance);
    }

    [Fact]
    public void InlineSsaInlinesSmallModulePrivateDirectCallsWithoutExplicitInline()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                finite law i32[min max] AddOne(i32[min max] value)
                {
                    return value + 1;
                }

                unsafe fn i32[min max] Run()
                {
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

                public finite law i32[min max] AddOne(i32[min max] value)
                {
                    return value + 1;
                }
                """);

            var pipeline = DefaultCompilerPipeline.Create();
            var result = pipeline.Run(
                new CompilationInput(
                    """
                    import Math
                    module Demo

                    unsafe fn i32[min max] Run()
                    {
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

                fn T Identity<T>(T value)
                {
                    return value;
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

            fn i32[min max] Run(i32[min max] value)
            {
                return value;
            }
            """);
        var generic = CompileRunFunction(
            """
            module Demo

            fn T Identity<T>(T value)
            {
                return value;
            }

            fn i32[min max] Run(i32[min max] value)
            {
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

                noinline fn T Identity<T>(T value)
                {
                    return value;
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

                public fn i32[min max] AddOne(i32[min max] value)
                {
                    unsafe
                    {
                        Touch();
                    }

                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

                public fn i32[min max] Double(i32[min max] value)
                {
                    return value * 2;
                }

                unsafe fn i32[min max] Run()
                {
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

                public fn i32[min max] AddOne(i32[min max] value)
                {
                    unsafe
                    {
                        Touch();
                    }

                    return value + 1;
                }

                unsafe fn i32[min max] Run()
                {
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

                public fn i32[min max] AddOne(i32[min max] value)
                {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

                public finite law i32[min max] AddOne(i32[min max] value)
                {
                    return value + 1;
                }

                unsafe fn i32[min max] Run()
                {
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

                inline finite law i32[min max] AddOne(i32[min max] value)
                {
                    return value + 1;
                }

                inline finite law i32[min max] Forward(i32[min max] value)
                {
                    return AddOne(value);
                }

                unsafe fn i32[min max] Run()
                {
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

                noinline finite law i32[min max] AddOne(i32[min max] value)
                {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value)
                {
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
    public void ConstGraphCallCseReusesRepeatedFiniteLawReadsFromConstGraphs()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                noinline unsafe finite law i32[min max] Read(const rawmutptr<i32[min max]> ptr)
                {
                    return *ptr;
                }

                unsafe fn i32[min max] Run(const rawmutptr<i32[min max]> ptr)
                {
                    stack i32[min max] first = Read(ptr);
                    stack i32[min max] second = Read(ptr);
                    return first + second;
                }
                """),
            new CompilerOptions(StopAfterPassId: "cse-const-graph-calls-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Equal(1, CountValueCalls(run, "Read"));
    }

    [Fact]
    public void ConstGraphCallCseKeepsRepeatedReadonlyPointerLocalReadsWithoutPermanentConst()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                noinline unsafe finite law i32[min max] Read(rawptr<i32[min max]> ptr)
                {
                    return *ptr;
                }

                unsafe fn i32[min max] Run(rawmutptr<i32[min max]> ptr)
                {
                    stack rawptr<i32[min max]> readonlyPtr = (rawptr<i32[min max]>)ptr;
                    stack i32[min max] first = Read(readonlyPtr);
                    stack i32[min max] second = Read(readonlyPtr);
                    return first + second;
                }
                """),
            new CompilerOptions(StopAfterPassId: "cse-const-graph-calls-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Equal(2, CountValueCalls(run, "Read"));
    }

    [Fact]
    public void ConstGraphCallCseKeepsRepeatedNonFiniteLawReads()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                noinline unsafe law i32[min max] Read(const rawmutptr<i32[min max]> ptr);

                unsafe fn i32[min max] Run(const rawmutptr<i32[min max]> ptr)
                {
                    stack i32[min max] first = Read(ptr);
                    stack i32[min max] second = Read(ptr);
                    return first + second;
                }
                """),
            new CompilerOptions(StopAfterPassId: "cse-const-graph-calls-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Equal(2, CountValueCalls(run, "Read"));
    }

    [Fact]
    public void ConstGraphCallCseKeepsRepeatedFfiReads()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                unsafe ffi fn i32[min max] Read(const rawmutptr<i32[min max]> ptr);

                unsafe fn i32[min max] Run(const rawmutptr<i32[min max]> ptr)
                {
                    stack i32[min max] first = Read(ptr);
                    stack i32[min max] second = Read(ptr);
                    return first + second;
                }
                """),
            new CompilerOptions(StopAfterPassId: "cse-const-graph-calls-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        Assert.Equal(2, CountValueCalls(run, "Read"));
    }

    [Fact]
    public void ValueFactsCaptureIntegerRangesAndProvenComparisons()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn bool Run(u8[0 10] value)
                {
                    return value < 20;
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "value-facts"));

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

                fn bool Run(u8[0 10] value)
                {
                    return value < 20;
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "value-facts"));

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
        Assert.True(log.Data.ContainsKey("dynamicStorageRegions"));
    }

    [Fact]
    public void ValueFactsJoinIntegerRangesAtPhis()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn u8[0 10] Choose(bool flag, u8[2 3] left, u8[4 5] right)
                {
                    stack mut u8[0 10] result = left;
                    if (flag)
                    {
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

                fn i32[min max] AddAfterJoin(bool flag, u8[0 10] left, u8[20 30] right)
                {
                    stack mut i32[min max] value = left;
                    if (flag)
                    {
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

                fn u8[0 max] Combine(u8[0 15] value, u8[0 2] amount)
                {
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

                fn u8[0 max] Mask(u8[0 7] value)
                {
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

                fn i32[min max] Mask(i32[min max] value)
                {
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

                fn u8[0 max] MaskShifted(u8[0 7] value)
                {
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

                fn bool ForcedBitCannotEqualZero(u8[0 7] value)
                {
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

                fn i32[min max] Divide(u8[0 100] value)
                {
                    return value / 10;
                }

                fn i32[min max] Modulo(u8[0 100] value)
                {
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

                fn i32[min max] Run(u8[0 7] value)
                {
                    stack u8[0 max] masked = (u8[0 max])(value & 8);
                    if (masked == 0)
                    {
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

                fn i32[min max] Run(u8[0 7] value)
                {
                    stack u16[0 4095] forced = (u16[0 4095])(value | 8);
                    if (forced == 0)
                    {
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

                fn i32[min max] Run(u8[0 100] value)
                {
                    stack i32[min max] slot = value % 8;
                    if (slot >= 8)
                    {
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

                fn i32[min max] Run(u8[0 7] value)
                {
                    stack u8[0 max] shifted = (u8[0 max])(value << 1);
                    stack u8[0 max] masked = (u8[0 max])(shifted & 1);
                    if (masked == 0)
                    {
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

                fn i32[min max] Run(u8[0 100] value)
                {
                    if (value < 10)
                    {
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

                fn i32[min max] Run(u8[0 100] value)
                {
                    if (value < 10)
                    {
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

                unsafe fn i32[min max] Run(rawptr<i32[min max]> ptr)
                {
                    if (ptr != null)
                    {
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

                unsafe fn i32[min max] Run(rawmutptr<i32[min max]> ptr)
                {
                    stack mut i32[min max] local = 1;
                    stack rawmutptr<i32[min max]> localPtr = &local;

                    if (ptr == localPtr)
                    {
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

                unsafe fn i32[min max] Run(rawmutptr<i32[min max]> ptr)
                {
                    stack mut i32[min max] local = 1;
                    stack rawmutptr<i32[min max]> localPtr = &local;

                    if (ptr != localPtr)
                    {
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

                unsafe fn i32[min max] Run()
                {
                    stack mut i32[min max] value = 1;
                    stack rawmutptr<i32[min max]> ptr = &value;
                    return *ptr;
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "cleanup-ssa"));

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

                fn i32[min max] Run(u8[0 2] index)
                {
                    stack i32[min max][3] values =
                    {
                        4, 7, 9
                    };
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

                fn i32[min max] Read(bool flag, u8[0 1] index)
                {
                    stack i32[min max][3] left =
                    {
                        1, 2, 3
                    };
                    stack i32[min max][5] right =
                    {
                        4, 5, 6, 7, 8
                    };
                    stack mut i32[min max][] view = left;
                    if (flag)
                    {
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

                fn unicode Run(unicode text, u8[2 5] length)
                {
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
    public void ValueFactsCaptureDynamicStorageSliceLengthRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                unsafe fn u32[0 max] Run()
                {
                    stack mut dynamic u32[0 max] values = new(4);
                    init values[0] = 10;
                    init values[1] = 20;
                    stack u32[0 max][] view = values[0, values.Length];
                    return view[1];
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
                           && HasLengthRange(fact, 2, 2));
    }

    [Fact]
    public void ValueFactsCaptureTextLiteralLengthCallRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                fn u64[0 2 ** 63 - 1] Run()
                {
                    return AsciiLength("stark") + UnicodeLength((unicode)"llvm");
                }

                public finite law u64[0 2 ** 63 - 1] AsciiLength(ascii source);
                public finite law u64[0 2 ** 63 - 1] UnicodeLength(unicode source);
                """),
            new CompilerOptions(
                StopAfterPassId: "cleanup-ssa"));

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

                fn u64[0 2 ** 63 - 1] Run()
                {
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
    public void ConstStdlibHelperSpecializationPrecomputesPathFactsForLiteral()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.IO.Path

                public struct PathFacts
                {
                    internal ascii Path;
                    internal i64[min max] Length;
                    internal i64[min max] End;
                    internal i64[min max] SegmentStart;
                    internal i64[min max] ExtensionStart;
                    internal i64[min max] DirectoryLength;
                    internal bool HasExtension;
                }

                public finite law PathFacts GetConstFacts(const ascii path);

                fn PathFacts Run()
                {
                    return GetConstFacts("alpha/beta.txt");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

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
            static call => call.FunctionName.Contains("GetConstFacts", StringComparison.Ordinal));

        var pathFactsFields = run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaInsertFieldRValue>()
            .ToArray();

        AssertPathFact(pathFactsFields, "Length", 14);
        AssertPathFact(pathFactsFields, "End", 14);
        AssertPathFact(pathFactsFields, "SegmentStart", 6);
        AssertPathFact(pathFactsFields, "ExtensionStart", 10);
        AssertPathFact(pathFactsFields, "DirectoryLength", 5);
        Assert.Contains(
            pathFactsFields,
            static field => field.FieldName == "HasExtension"
                            && field.Value is SsaBoolConstant { Value: true });
    }

    [Fact]
    public void ConstStdlibHelperSpecializationPrecomputesLiteralPathProjections()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.IO.Path

                public finite law ascii Extension(ascii path);
                public finite law ascii BaseNameConst(const ascii path);
                public finite law ascii DirectoryNameConst(const ascii path);

                fn ascii ExtensionRun()
                {
                    return Extension("archive.tar.gz");
                }

                fn ascii BaseNameRun()
                {
                    return BaseNameConst("alpha/beta.txt");
                }

                fn ascii DirectoryRun()
                {
                    return DirectoryNameConst("alpha/beta.txt");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertReturnedAsciiLiteral(ssa, "ExtensionRun", "\".gz\"");
        AssertReturnedAsciiLiteral(ssa, "BaseNameRun", "\"beta\"");
        AssertReturnedAsciiLiteral(ssa, "DirectoryRun", "\"alpha\"");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationKeepsRuntimePathInputsOnStdlibPath()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.IO.Path

                public struct PathFacts
                {
                    internal ascii Path;
                    internal i64[min max] Length;
                    internal i64[min max] End;
                    internal i64[min max] SegmentStart;
                    internal i64[min max] ExtensionStart;
                    internal i64[min max] DirectoryLength;
                    internal bool HasExtension;
                }

                public finite law PathFacts GetFacts(ascii path);
                public finite law ascii Extension(ascii path);

                fn PathFacts FactsRun(ascii path)
                {
                    return GetFacts(path);
                }

                fn ascii ExtensionRun(ascii path)
                {
                    return Extension(path);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var calls = ssa.Functions
            .Where(static function => function.Name is "FactsRun" or "ExtensionRun")
            .SelectMany(static function => function.Blocks)
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>()
            .ToArray();

        Assert.Contains(calls, static call => call.FunctionName.Contains("GetFacts", StringComparison.Ordinal));
        Assert.Contains(calls, static call => call.FunctionName.Contains("Extension", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstStdlibHelperSpecializationRetargetsLiteralPathWritesToConstVariants()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.IO.Path

                public struct OwnedAscii
                {
                }

                public fn bool TryJoin(mut borrow OwnedAscii destination, ascii left, ascii right);
                public fn bool TryJoinConst(mut borrow OwnedAscii destination, const ascii left, const ascii right);

                public fn bool TryNormalizeSeparators(mut borrow OwnedAscii destination, ascii path);
                public fn bool TryNormalizeSeparatorsConst(mut borrow OwnedAscii destination, const ascii path);

                fn bool JoinRun(mut borrow OwnedAscii destination)
                {
                    return TryJoin(destination, "alpha", "beta.txt");
                }

                fn bool NormalizeRun(mut borrow OwnedAscii destination)
                {
                    return TryNormalizeSeparators(destination, "alpha//beta.txt");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertRetargetedCall(ssa, "JoinRun", "TryJoinConst");
        AssertRetargetedCall(ssa, "NormalizeRun", "TryNormalizeSeparatorsConst");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationRetargetsConstPathWritesToConstVariants()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.IO.Path

                public struct OwnedAscii
                {
                }

                public fn bool TryJoin(mut borrow OwnedAscii destination, ascii left, ascii right);
                public fn bool TryJoinConst(mut borrow OwnedAscii destination, const ascii left, const ascii right);

                public fn bool TryNormalizeSeparators(mut borrow OwnedAscii destination, ascii path);
                public fn bool TryNormalizeSeparatorsConst(mut borrow OwnedAscii destination, const ascii path);

                fn bool JoinRun(mut borrow OwnedAscii destination, const ascii left, const ascii right)
                {
                    return TryJoin(destination, left, right);
                }

                fn bool NormalizeRun(mut borrow OwnedAscii destination, const ascii path)
                {
                    return TryNormalizeSeparators(destination, path);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertRetargetedCall(ssa, "JoinRun", "TryJoinConst");
        AssertRetargetedCall(ssa, "NormalizeRun", "TryNormalizeSeparatorsConst");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationKeepsRuntimePathWritesOnSnapshotCapableVariants()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.IO.Path

                public struct OwnedAscii
                {
                }

                public fn bool TryJoin(mut borrow OwnedAscii destination, ascii left, ascii right);
                public fn bool TryJoinConst(mut borrow OwnedAscii destination, const ascii left, const ascii right);

                fn bool Run(mut borrow OwnedAscii destination, ascii left)
                {
                    return TryJoin(destination, left, "beta.txt");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertRetargetedCall(ssa, "Run", "TryJoin");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationRetargetsLiteralTextAppendsToConstDisjointVariants()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public struct OwnedAscii
                {
                }
                public struct OwnedUnicode
                {
                }

                public fn bool AppendAscii(mut borrow OwnedAscii destination, ascii source);
                public fn bool AppendConstAsciiDisjoint(mut borrow OwnedAscii destination, const ascii source);

                public fn bool AppendUnicode(mut borrow OwnedUnicode destination, unicode source);
                public fn bool AppendConstUnicodeDisjoint(mut borrow OwnedUnicode destination, const unicode source);

                fn bool AppendAsciiRun(mut borrow OwnedAscii destination)
                {
                    return AppendAscii(destination, "alpha");
                }

                fn bool AppendUnicodeRun(mut borrow OwnedUnicode destination)
                {
                    return AppendUnicode(destination, (unicode)"beta");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertRetargetedCall(ssa, "AppendAsciiRun", "AppendConstAsciiDisjoint");
        AssertRetargetedCall(ssa, "AppendUnicodeRun", "AppendConstUnicodeDisjoint");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationRetargetsConstTextHelpers()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public struct OwnedAscii
                {
                }
                public struct OwnedUnicode
                {
                }

                public fn bool AppendAscii(mut borrow OwnedAscii destination, ascii source);
                public fn bool AppendConstAsciiDisjoint(mut borrow OwnedAscii destination, const ascii source);

                public fn bool AppendUnicode(mut borrow OwnedUnicode destination, unicode source);
                public fn bool AppendConstUnicodeDisjoint(mut borrow OwnedUnicode destination, const unicode source);

                public fn bool FromAscii(out OwnedAscii destination, ascii source);
                public fn bool FromConstAscii(out OwnedAscii destination, const ascii source);

                public fn bool FromAsciiToUnicode(out OwnedUnicode destination, ascii source);
                public fn bool FromConstAsciiToUnicode(out OwnedUnicode destination, const ascii source);

                fn bool AppendAsciiRun(mut borrow OwnedAscii destination, const ascii source)
                {
                    return AppendAscii(destination, source);
                }

                fn bool AppendUnicodeRun(mut borrow OwnedUnicode destination, const unicode source)
                {
                    return AppendUnicode(destination, source);
                }

                fn bool FromAsciiRun(out OwnedAscii destination, const ascii source)
                {
                    return FromAscii(destination, source);
                }

                fn bool FromAsciiToUnicodeRun(out OwnedUnicode destination, const ascii source)
                {
                    return FromAsciiToUnicode(destination, source);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertRetargetedCall(ssa, "AppendAsciiRun", "AppendConstAsciiDisjoint");
        AssertRetargetedCall(ssa, "AppendUnicodeRun", "AppendConstUnicodeDisjoint");
        AssertRetargetedCall(ssa, "FromAsciiRun", "FromConstAscii");
        AssertRetargetedCall(ssa, "FromAsciiToUnicodeRun", "FromConstAsciiToUnicode");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationRetargetsLiteralTextMemberAppends()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public struct OwnedAscii
                {
                    noinline fn bool AppendAscii(mut borrow OwnedAscii self, ascii source)
                    {
                        return false;
                    }

                    noinline fn bool AppendConstAsciiDisjoint(mut borrow OwnedAscii self, const ascii source)
                    {
                        return true;
                    }
                }

                fn bool Run(mut borrow OwnedAscii destination)
                {
                    return destination.AppendAscii("alpha");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertRetargetedCall(ssa, "Run", "OwnedAscii.AppendConstAsciiDisjoint");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationKeepsRuntimeTextAppendsOnSnapshotCapableVariants()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public struct OwnedAscii
                {
                }

                public fn bool AppendAscii(mut borrow OwnedAscii destination, ascii source);
                public fn bool AppendConstAsciiDisjoint(mut borrow OwnedAscii destination, const ascii source);

                fn bool Run(mut borrow OwnedAscii destination, ascii source)
                {
                    return AppendAscii(destination, source);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertRetargetedCall(ssa, "Run", "AppendAscii");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationRetargetsLiteralTextFactoryHelpers()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public struct OwnedAscii
                {
                }
                public struct OwnedUnicode
                {
                }

                public fn bool FromAscii(out OwnedAscii destination, ascii source);
                public fn bool FromConstAscii(out OwnedAscii destination, const ascii source);

                public fn bool FromAsciiToUnicode(out OwnedUnicode destination, ascii source);
                public fn bool FromConstAsciiToUnicode(out OwnedUnicode destination, const ascii source);

                fn bool FromAsciiRun(out OwnedAscii destination)
                {
                    return FromAscii(destination, "alpha");
                }

                fn bool FromAsciiToUnicodeRun(out OwnedUnicode destination)
                {
                    return FromAsciiToUnicode(destination, "beta");
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-const-stdlib-helpers-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        AssertRetargetedCall(ssa, "FromAsciiRun", "FromConstAscii");
        AssertRetargetedCall(ssa, "FromAsciiToUnicodeRun", "FromConstAsciiToUnicode");
    }

    [Fact]
    public void ConstStdlibHelperSpecializationUsesWindowsPathSeparatorsForWindowsTargets()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.IO.Path

                public struct PathFacts
                {
                    internal ascii Path;
                    internal i64[min max] Length;
                    internal i64[min max] End;
                    internal i64[min max] SegmentStart;
                    internal i64[min max] ExtensionStart;
                    internal i64[min max] DirectoryLength;
                    internal bool HasExtension;
                }

                public finite law PathFacts GetConstFacts(const ascii path);

                fn PathFacts Run()
                {
                    return GetConstFacts("alpha\\beta.txt");
                }
                """),
            new CompilerOptions(
                StopAfterPassId: "specialize-const-stdlib-helpers-ssa",
                TargetInfo: new LlvmTargetInfo("x86_64-pc-windows-msvc", null)));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
        var pathFactsFields = run.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaInsertFieldRValue>()
            .ToArray();

        AssertPathFact(pathFactsFields, "SegmentStart", 6);
        AssertPathFact(pathFactsFields, "DirectoryLength", 5);
    }

    [Fact]
    public void ConstLookupTableOptimizationFoldsPackageConstFixedArrayIndexFromTypedInitializer()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-const-lookup-ssa-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public const i32[min max][4] Lookup =
                {
                    10, 20, 30, 40
                };
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

                    unsafe fn i32[min max] Run()
                    {
                        return Facade.Lookup[2];
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "const-lookup-tables-ssa"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
            Assert.NotNull(ssa);

            var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
            Assert.DoesNotContain(
                run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>().Select(static instruction => instruction.Value),
                static value => value is SsaLoadGlobalRValue { GlobalName: "Facade.Lookup" });
            var resultValue = Assert.IsType<SsaIntegerConstant>(Assert.Single(run.Blocks).Terminator.Value);
            Assert.Equal(new System.Numerics.BigInteger(30), resultValue.Value);
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
    public void ConstLookupTableOptimizationFoldsPackageConstLookupHelperFromTypedInitializer()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-const-helper-lookup-ssa-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public const i32[min max][4] Lookup =
                {
                    10, 20, 30, 40
                };
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
                    import System.Collections
                    module Demo

                    unsafe fn i32[min max] Run()
                    {
                        return System.Collections.Lookup(Facade.Lookup, 2);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver([tempDirectory.FullName, StandardLibrarySourceRoot()]),
                    StopAfterPassId: "const-lookup-tables-ssa"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
            Assert.NotNull(ssa);

            var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
            Assert.DoesNotContain(
                run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>().Select(static instruction => instruction.Value),
                static value => value is SsaLoadGlobalRValue { GlobalName: "Facade.Lookup" });
            var resultValue = Assert.IsType<SsaIntegerConstant>(Assert.Single(run.Blocks).Terminator.Value);
            Assert.Equal(new System.Numerics.BigInteger(30), resultValue.Value);
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
    public void ConstLookupTableOptimizationKeepsConstLookupHelperWithRuntimeIndexAsLoad()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-const-helper-runtime-lookup-ssa-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public const i32[min max][4] Lookup =
                {
                    10, 20, 30, 40
                };
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
                    import System.Collections
                    module Demo

                    unsafe fn i32[min max] Run(u64[0 2 ** 63 - 1] index)
                    {
                        return System.Collections.Lookup(Facade.Lookup, index);
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver([tempDirectory.FullName, StandardLibrarySourceRoot()]),
                    StopAfterPassId: "const-lookup-tables-ssa"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
            Assert.NotNull(ssa);

            var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
            Assert.IsNotType<SsaIntegerConstant>(Assert.Single(run.Blocks).Terminator.Value);
            Assert.Contains(
                run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>().Select(static instruction => instruction.Value),
                static value => value is SsaLoadIndirectRValue);
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
    public void ConstLookupTableOptimizationKeepsStaticFixedArrayIndexAsGlobalLoad()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("stark-package-static-lookup-ssa-");
        var manifestPath = Path.Combine(tempDirectory.FullName, "libFacade.starkpkg.json");
        var facadePath = Path.Combine(tempDirectory.FullName, "Facade.stark");

        try
        {
            var pipeline = DefaultCompilerPipeline.Create();
            var libraryResult = pipeline.Run(new CompilationInput(
                """
                module Facade

                public static i32[min max][4] Lookup =
                {
                    10, 20, 30, 40
                };
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

                    unsafe fn i32[min max] Run()
                    {
                        return Facade.Lookup[2];
                    }
                    """,
                    Path.Combine(tempDirectory.FullName, "Demo.stark")),
                new CompilerOptions(
                    ModuleResolver: new FileSystemModuleResolver(tempDirectory.FullName),
                    StopAfterPassId: "const-lookup-tables-ssa"));

            Assert.True(consumerResult.Succeeded, string.Join(", ", consumerResult.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
            Assert.True(consumerResult.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
            Assert.NotNull(ssa);

            var run = Assert.Single(ssa.Functions, static function => function.Name == "Run");
            Assert.Contains(
                run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaValueInstruction>().Select(static instruction => instruction.Value),
                static value => value is SsaLoadGlobalRValue { GlobalName: "Facade.Lookup" });
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
    public void ValueFactsCaptureExplicitWrappingAndSaturatingArithmeticRanges()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[0 10] left, u8[0 5] right)
                {
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

                fn i32[min max] Run(u8[0 10] value)
                {
                    stack mut i32[min max] result = 0;
                    if (value < 20)
                    {
                        result = 1;
                    }
                    else
                    {
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

                fn i32[min max] Run(u8[0 100] value)
                {
                    if (value < 10)
                    {
                        if (value >= 10)
                        {
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

                unsafe fn i32[min max] Run(rawptr<i32[min max]> ptr)
                {
                    if (ptr != null)
                    {
                        if (ptr == null)
                        {
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

                unsafe fn i32[min max] Run(rawmutptr<i32[min max]> ptr)
                {
                    stack mut i32[min max] local = 1;
                    stack rawmutptr<i32[min max]> localPtr = &local;

                    if (ptr == localPtr)
                    {
                        if (ptr == null)
                        {
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

                fn i32[min max] Run(u8[0 15] value)
                {
                    stack u8[0 max] masked = (u8[0 max])(value & 7);
                    if (masked < 8)
                    {
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

                fn i32[min max] Run(u8[0 10] left, u8[0 5] right)
                {
                    stack i32[min max] saturated = left +| right;
                    stack i32[min max] wrapped = left +% right;

                    if (saturated > 15)
                    {
                        return 1;
                    }

                    if (wrapped > 15)
                    {
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

                fn i32[min max] Run(i8[-1 max] value)
                {
                    stack i8[min max] wrapped = value +% 1;
                    if (wrapped < 0)
                    {
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

                unsafe fn i32[min max] Run()
                {
                    if (AsciiLength("stark") == 5)
                    {
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

                public unsafe fn bool Run()
                {
                    stack mut i32[min max][16] unicodeBuffer =
                    {
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                    };
                    stack mut Unicode ownedUnicode = new Unicode()
                    {
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

                public unsafe fn bool Run()
                {
                    stack mut i32[min max][64] unicodeBuffer =
                    {
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                    };
                    stack mut Unicode ownedUnicode = new Unicode()
                    {
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

                public unsafe fn i32[min max] Run(i32[min max] count)
                {
                    stack mut i32[min max][16] unicodeBuffer =
                    {
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                    };
                    stack mut Unicode ownedUnicode = new Unicode()
                    {
                        Data = &unicodeBuffer[0],
                        Length = 0,
                        Capacity = 16
                    };
                    stack mut i32[min max] index = 0;

                    while willexit (index < count)
                    {
                        if (TryConvertAsciiToUnicode(&ownedUnicode, "Stark") == false)
                        {
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

                public unsafe fn bool Run()
                {
                    stack mut i32[min max][16] unicodeBuffer =
                    {
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
                    };
                    stack mut Unicode ownedUnicode = new Unicode()
                    {
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
    public void ConstantTextFormatSpecializationRewritesLargeIntegerAsciiFormatCallsBeforeAbiLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe finite bool TryFormatI1024Ascii(rawmutptr<Ascii> destination, i1024[min max] value);

                public unsafe fn bool Run()
                {
                    stack mut i8[min max][320] storage;
                    stack mut Ascii text = new Ascii()
                    {
                        Data = &storage[0],
                        Length = 0,
                        Capacity = 320
                    };

                    return TryFormatI1024Ascii(&text, (i1024[min max])(-(2 ** 1023)));
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-constant-text-formatting-ssa"));

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
            static call => call.FunctionName.Contains("TryFormatI1024Ascii", StringComparison.Ordinal));
        Assert.Contains(run.Blocks, static block => block.Label.Contains("format_const", StringComparison.Ordinal));
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaCopyMemoryInstruction>(),
            static copy => copy.SourceAddress is SsaTextDataAddressValue { TextType.Kind: StarkTypeKind.Ascii }
                           && copy.CopyType is { Kind: StarkTypeKind.FixedArray, FixedLength: 309, ElementType.BitWidth: 8 });
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreIndirectInstruction>(),
            static store => store.Value is SsaIntegerConstant integer
                            && integer.Value == new System.Numerics.BigInteger(309));
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Phis),
            static phi => phi.Type.Kind == StarkTypeKind.Bool && phi.Incomings.Count == 3);
    }

    [Fact]
    public void ConstantTextFormatSpecializationRewritesLargeIntegerUnicodeFormatCallsBeforeAbiLowering()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe finite bool TryFormatU1024Unicode(rawmutptr<Unicode> destination, u1024[0 max] value);

                public unsafe fn bool Run()
                {
                    stack mut i32[min max][320] storage;
                    stack mut Unicode text = new Unicode()
                    {
                        Data = &storage[0],
                        Length = 0,
                        Capacity = 320
                    };

                    return TryFormatU1024Unicode(&text, (u1024[0 max])(2 ** 1024 - 1));
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-constant-text-formatting-ssa"));

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
            static call => call.FunctionName.Contains("TryFormatU1024Unicode", StringComparison.Ordinal));
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaCopyMemoryInstruction>(),
            static copy => copy.SourceAddress is SsaTextDataAddressValue { TextType.Kind: StarkTypeKind.Unicode }
                           && copy.CopyType is { Kind: StarkTypeKind.FixedArray, FixedLength: 309, ElementType.BitWidth: 32 });
    }

    [Fact]
    public void ConstantTextFormatSpecializationKeepsRuntimeIntegerFormatCallsOnStdLibPath()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe finite bool TryFormatI1024Ascii(rawmutptr<Ascii> destination, i1024[min max] value);

                public unsafe fn bool Run(i1024[min max] value)
                {
                    stack mut i8[min max][320] storage;
                    stack mut Ascii text = new Ascii()
                    {
                        Data = &storage[0],
                        Length = 0,
                        Capacity = 320
                    };

                    return TryFormatI1024Ascii(&text, value);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-constant-text-formatting-ssa"));

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
            static call => call.FunctionName.Contains("TryFormatI1024Ascii", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantTextFormatSpecializationKeepsReadonlyDestinationIntegerFormatCalls()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe finite bool TryFormatI1024Ascii(rawptr<Ascii> destination, i1024[min max] value);

                public unsafe fn bool Run()
                {
                    stack mut i8[min max][320] storage;
                    stack mut Ascii text = new Ascii()
                    {
                        Data = &storage[0],
                        Length = 0,
                        Capacity = 320
                    };

                    return TryFormatI1024Ascii(&text, (i1024[min max])123);
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-constant-text-formatting-ssa"));

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
            static call => call.FunctionName.Contains("TryFormatI1024Ascii", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstantTextFormatSpecializationNormalizesOutOfRangeNarrowedIntegerConstants()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module System.Text

                public unsafe finite bool TryFormatI16Ascii(rawmutptr<Ascii> destination, i16[min max] value);

                public unsafe fn bool Run()
                {
                    stack mut i8[min max][8] storage;
                    stack mut Ascii text = new Ascii()
                    {
                        Data = &storage[0],
                        Length = 0,
                        Capacity = 8
                    };

                    return TryFormatI16Ascii(&text, (i16[min max])((i8[min max])300));
                }
                """),
            new CompilerOptions(StopAfterPassId: "specialize-constant-text-formatting-ssa"));

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
            static call => call.FunctionName.Contains("TryFormatI16Ascii", StringComparison.Ordinal));
        Assert.Contains(
            run.Blocks.SelectMany(static block => block.Instructions).OfType<SsaStoreIndirectInstruction>(),
            static store => store.Value is SsaIntegerConstant integer
                            && integer.Value == new System.Numerics.BigInteger(2));
        Assert.True(
            run.Blocks
                .SelectMany(static block => block.Instructions)
                .OfType<SsaStoreIndirectInstruction>()
                .Count(static store => store.Value is SsaIntegerConstant integer
                                       && integer.Value == new System.Numerics.BigInteger(52)) >= 2);
    }

    [Fact]
    public void FactDrivenSwitchPruningRemovesCasesOutsideKnownInputRange()
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(
                """
                module Demo

                fn i32[min max] Run(u8[0 2] value)
                {
                    stack i32[min max] widened = (i32[min max])value;
                    switch (widened)
                    {
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

                fn i32[min max] Run(u8[10 12] value)
                {
                    stack i32[min max] widened = (i32[min max])value;
                    switch (widened)
                    {
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

                unsafe fn i32[min max] Run()
                {
                    stack mut u24[0 200000] total = 0;
                    for willexit (stack mut u8[0 2] i = 0; i < 2; i += 1)
                    {
                        total += 1;
                    }

                    for willexit (stack mut u24[0 100000] i = 0; i < 100000; i += 1)
                    {
                        total += 1;
                    }

                    if (total == 100002)
                    {
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

                unsafe fn i32[min max] Run(i32[min max] value)
                {
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

                unsafe fn i32[min max] Run(i32[min max] value)
                {
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

                unsafe fn i32[min max] Run(i32[min max] value)
                {
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

                record Pair(i32[min max] Left, i32[min max] Right)
                {
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

                record Inner(i32[min max] Value, i32[min max] Salt)
                {
                }
                record Outer(Inner Left, Inner Right)
                {
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

                record Pair(i32[min max] Left, i32[min max] Right)
                {
                }

                noinline finite law i32[min max] Touch(i32[min max] value)
                {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

                record Pair(i32[min max] Left, i32[min max] Right)
                {
                }

                fn i32[min max] Run(bool flag, i32[min max] value)
                {
                    stack mut Pair pair = new Pair(0, 1);
                    pair.Left = value;
                    if (flag)
                    {
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

                fn u8[0 200] Run(u8[0 100] value)
                {
                    stack mut u8[0 200][4] items =
                    {
                        0, 0, 0, 0
                    };
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

                record Pair(i32[min max] Left, i32[min max] Right)
                {
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

                noinline finite law i32[min max] Touch(i32[min max] value)
                {
                    return value + 1;
                }

                fn i32[min max] Run(i32[min max] value)
                {
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

    private static void AssertPathFact(
        IReadOnlyList<SsaInsertFieldRValue> fields,
        string fieldName,
        int expectedValue)
    {
        Assert.Contains(
            fields,
            field => field.FieldName == fieldName
                     && field.Value is SsaIntegerConstant integer
                     && integer.Value == new System.Numerics.BigInteger(expectedValue));
    }

    private static void AssertReturnedAsciiLiteral(
        SsaIrModule ssa,
        string functionName,
        string expectedLiteralText)
    {
        var function = Assert.Single(ssa.Functions, candidate => candidate.Name == functionName);
        var block = Assert.Single(function.Blocks);
        var value = Assert.IsType<SsaStringConstant>(block.Terminator.Value);
        Assert.Equal(expectedLiteralText, value.LiteralText);
    }

    private static void AssertRetargetedCall(
        SsaIrModule ssa,
        string functionName,
        string expectedCallName)
    {
        var function = Assert.Single(ssa.Functions, candidate => candidate.Name == functionName);
        var call = Assert.Single(function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>());
        Assert.Equal(expectedCallName, call.FunctionName);
    }

    private static int CountValueCalls(SsaFunction function, string functionName)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaCallRValue>()
            .Count(call => string.Equals(call.FunctionName, functionName, StringComparison.Ordinal));
    }

    private static SsaFunction CompileRunAfterArithmeticFold(string source)
    {
        var pipeline = DefaultCompilerPipeline.Create();
        var result = pipeline.Run(
            new CompilationInput(source),
            new CompilerOptions(StopAfterPassId: "arithmetic-fold-ssa"));

        Assert.True(result.Succeeded, string.Join(", ", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.OptimizedSsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);
        return Assert.Single(ssa.Functions, static function => function.Name == "Run");
    }

    private static IReadOnlyList<SsaBinaryRValue> GetBinaryRValues(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .Select(static instruction => instruction.Value)
            .OfType<SsaBinaryRValue>()
            .ToArray();
    }

    private static IReadOnlyDictionary<string, SsaRValue> CollectSsaDefinitions(SsaFunction function)
    {
        return function.Blocks
            .SelectMany(static block => block.Instructions)
            .OfType<SsaValueInstruction>()
            .ToDictionary(static instruction => instruction.ResultName, static instruction => instruction.Value, StringComparer.Ordinal);
    }

    private static bool IsDynamicLengthAddress(
        SsaValue value,
        IReadOnlyDictionary<string, SsaRValue> definitions)
    {
        return value is SsaValueReference reference
               && definitions.TryGetValue(reference.Name, out var definition)
               && definition is SsaFieldAddressRValue
               {
                   FieldName: "Length",
                   AggregateType.Kind: StarkTypeKind.Dynamic
               };
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
