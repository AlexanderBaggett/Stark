using Stark.Compiler;

namespace compiler.FeatureTests;

public sealed class FunctionKindsFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void LawFunctionEmitsLawShapedLlvmAttributes()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            law i32[min max] Run()
            {
                return 1;
            }
            """);

        Assert.Contains("define fastcc noundef i32 @Run()", llvm);
        Assert.Contains("nounwind", llvm);
        Assert.Contains("nosync", llvm);
        Assert.Contains("nofree", llvm);
        Assert.Contains("memory(none)", llvm);
    }

    [Fact]
    public void TailFunctionBecomeEmitsMustTailUnderTailcc()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            tail fn i32[min max] Done(i32[min max] value)
            {
                return value;
            }

            tail fn i32[min max] Bounce(i32[min max] value)
            {
                become Done(value);
            }
            """);

        var doneHeader = ExtractDefinitionHeader(llvm, "Done");
        var bounceHeader = ExtractDefinitionHeader(llvm, "Bounce");
        var bounceBody = ExtractDefinitionBody(llvm, "Bounce");

        Assert.Contains("define tailcc noundef i32 @Done(", doneHeader);
        Assert.Contains("define tailcc noundef i32 @Bounce(", bounceHeader);
        Assert.Contains("musttail call tailcc i32 @Done(", bounceBody);
        Assert.Contains("ret i32", bounceBody);
    }

    [Fact]
    public void BecomeLowersToMirAndSsaTailCallTerminators()
    {
        var result = Compile(
            """
            module Demo

            tail fn i32[min max] Done(i32[min max] value)
            {
                return value;
            }

            tail fn i32[min max] Bounce(i32[min max] value)
            {
                become Done(value);
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.MidLevelIr, out MidLevelIrModule? mir));
        Assert.NotNull(mir);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SsaIr, out SsaIrModule? ssa));
        Assert.NotNull(ssa);

        var mirBounce = Assert.Single(mir.Functions, static function => function.Name == "Bounce");
        Assert.Contains(
            mirBounce.Blocks,
            static block => block.Terminator.Kind == MidLevelIrTerminatorKind.TailCall
                && block.Terminator.TailCall?.Text.Contains("Done(", StringComparison.Ordinal) == true);

        var ssaBounce = Assert.Single(ssa.Functions, static function => function.Name == "Bounce");
        Assert.Contains(
            ssaBounce.Blocks,
            static block => block.Terminator.Kind == SsaTerminatorKind.TailCall
                && block.Terminator.TailDirectCall?.Text.Contains("Done(", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void MutualTailFunctionsEmitMustTailOnBothEdges()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            tail fn i32[min max] Left(i32[min max] value)
            {
                become Right(value);
            }

            tail fn i32[min max] Right(i32[min max] value)
            {
                become Left(value);
            }
            """);

        var leftBody = ExtractDefinitionBody(llvm, "Left");
        var rightBody = ExtractDefinitionBody(llvm, "Right");

        Assert.Contains("define tailcc noundef i32 @Left(", ExtractDefinitionHeader(llvm, "Left"));
        Assert.Contains("define tailcc noundef i32 @Right(", ExtractDefinitionHeader(llvm, "Right"));
        Assert.Contains("musttail call tailcc i32 @Right(", leftBody);
        Assert.Contains("musttail call tailcc i32 @Left(", rightBody);
    }

    [Fact]
    public void TailCallableFunctionPointerBecomeEmitsIndirectMustTail()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            tail fn i32[min max] Done(i32[min max] value)
            {
                return value;
            }

            static mut fnptr<tail fn i32[min max](i32[min max])> Next = Done;

            tail fn i32[min max] Bounce(i32[min max] value)
            {
                become Next(value);
            }
            """);

        var bounceBody = ExtractDefinitionBody(llvm, "Bounce");

        Assert.Contains("define tailcc noundef i32 @Bounce(", ExtractDefinitionHeader(llvm, "Bounce"));
        Assert.Contains("musttail call tailcc i32 %", bounceBody);
        Assert.Contains("ret i32", bounceBody);
    }

    [Fact]
    public void TailCallableFunctionPointerParameterBecomeReportsAbiMismatchBeforeEmit()
    {
        var result = Compile(
            """
            module Demo

            tail fn i32[min max] Bad(fnptr<tail fn i32[min max](i32[min max])> next, i32[min max] value)
            {
                become next(value);
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK5002"
            && diagnostic.Stage == "validate-ssa"
            && diagnostic.Message.Contains("has 1 ABI parameter(s), but caller 'Bad' has 2", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK5001");
    }

    [Fact]
    public void BecomeRejectsPendingOwnedCleanupOnTailEdge()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                drop
                {
                }
            }

            tail fn i32[min max] Done(i32[min max] value)
            {
                return value;
            }

            tail fn i32[min max] Bad(i32[min max] value)
            {
                stack Box box = new Box()
                {
                    Value = value
                };
                become Done(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "ownership-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK4207"
            && diagnostic.Stage == "ownership-validate"
            && diagnostic.Message.Contains("box", StringComparison.Ordinal)
            && diagnostic.Message.Contains("implicitly dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void BecomeAllowsOwnedParameterMovedIntoTailCall()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;

                drop
                {
                }
            }

            tail fn i32[min max] Consume(Box box)
            {
                return box.Value;
            }

            tail fn i32[min max] Forward(Box box)
            {
                become Consume(box);
            }
            """,
            new CompilerOptions(StopAfterPassId: "ownership-validate"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BecomeRejectsBorrowOfCallerLocalStorage()
    {
        var result = Compile(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            tail fn i32[min max] Read(borrow Box box)
            {
                return box.Value;
            }

            tail fn i32[min max] Bad(i32[min max] value)
            {
                stack Box box = new Box()
                {
                    Value = value
                };
                become Read(box);
            }
            """,
            new CompilerOptions(StopAfterPassId: "ownership-validate"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK4207"
            && diagnostic.Stage == "ownership-validate"
            && diagnostic.Message.Contains("caller-local storage", StringComparison.Ordinal));
    }

    [Fact]
    public void TailDynTraitDispatchBecomeEmitsIndirectMustTail()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            dyn trait Stepper
            {
                tail fn i32[min max] Step(borrow Self self, i32[min max] value);
            }

            struct Done : Stepper
            {
                tail fn i32[min max] Step(borrow Done self, i32[min max] value)
                {
                    return value;
                }
            }

            tail fn i32[min max] Dispatch(borrow dyn Stepper stepper, i32[min max] value)
            {
                become stepper.Step(value);
            }
            """);

        var dispatchBody = ExtractDefinitionBody(llvm, "Dispatch");

        Assert.Contains("define tailcc noundef i32 @Dispatch(", ExtractDefinitionHeader(llvm, "Dispatch"));
        Assert.Contains("@__stark_vtable_Done__Stepper", llvm, StringComparison.Ordinal);
        Assert.Contains("getelementptr ptr,", dispatchBody, StringComparison.Ordinal);
        Assert.Contains("load ptr,", dispatchBody, StringComparison.Ordinal);
        Assert.Contains("musttail call tailcc i32 %", dispatchBody, StringComparison.Ordinal);
        Assert.Contains("ret i32", dispatchBody, StringComparison.Ordinal);
    }

    [Fact]
    public void TailModifierIsContextualKeyword()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] UseTailIdentifier(i32[min max] value)
            {
                stack mut i32[min max] tail = value;
                tail += 1;
                return tail;
            }
            """);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void BecomeRequiresTailFunctionAndTailCallableTarget()
    {
        var result = Compile(
            """
            module Demo

            fn i32[min max] Done(i32[min max] value)
            {
                return value;
            }

            fn i32[min max] Bounce(i32[min max] value)
            {
                become Done(value);
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("must be declared 'tail'", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("target of 'become' is not tail-callable", StringComparison.Ordinal));
    }

    [Fact]
    public void TailCallableFunctionPointerTypeIsPartOfTypeIdentity()
    {
        var result = Compile(
            """
            module Demo

            tail fn i32[min max] Done(i32[min max] value)
            {
                return value;
            }

            fn i32[min max] Use(fnptr<tail fn i32[min max](i32[min max])> callback, i32[min max] value)
            {
                return callback(value);
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeModel));
        Assert.NotNull(typeModel);
        var use = typeModel.Functions.Values.Single(function =>
            function.Parameters.Count == 2
            && function.Parameters[0].Type.Kind == StarkTypeKind.FunctionPointer);
        Assert.True(use.Parameters[0].Type.FunctionPointerIsTailCallable);
        Assert.Contains("fnptr<tail fn", use.Parameters[0].Type.DisplayName);
    }

    [Fact]
    public void TailTraitMethodConformanceRequiresMatchingTailContract()
    {
        var result = Compile(
            """
            module Demo

            trait Stepper
            {
                tail fn i32[min max] Step(borrow Self self, i32[min max] value);
            }

            struct Bad : Stepper
            {
                fn i32[min max] Step(borrow Bad self, i32[min max] value)
                {
                    return value;
                }
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3033"
            && diagnostic.Message.Contains("Bad.Step", StringComparison.Ordinal)
            && diagnostic.Message.Contains("must be declared 'tail'", StringComparison.Ordinal));
    }

    [Fact]
    public void TailCallableFunctionPointerTypeParticipatesInTraitConformance()
    {
        var result = Compile(
            """
            module Demo

            trait Handler
            {
                fn i32[min max] Invoke(
                    borrow Self self,
                    fnptr<tail fn i32[min max](i32[min max])> callback,
                    i32[min max] value);
            }

            struct Bad : Handler
            {
                fn i32[min max] Invoke(
                    borrow Bad self,
                    fnptr<fn i32[min max](i32[min max])> callback,
                    i32[min max] value)
                {
                    return callback(value);
                }
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK3033"
            && diagnostic.Message.Contains("does not match the parameter or return types", StringComparison.Ordinal));
    }

    [Fact]
    public void TailFfiAndVarargsUseFrontEndAbiDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            unsafe tail ffi varargs fn void Native(i32[min max] value);
            """);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK9999");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK4121"
            && diagnostic.Stage == "lower-abi"
            && diagnostic.Message.Contains("FFI", StringComparison.Ordinal)
            && diagnostic.Message.Contains("varargs", StringComparison.Ordinal));
    }

    [Fact]
    public void TailLargeReturnUseFrontEndAbiDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            tail fn Big Make(i64[min max] value)
            {
                return new Big()
                {
                    A = value,
                    B = value,
                    C = value
                };
            }
            """);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK9999");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK4121"
            && diagnostic.Stage == "lower-abi"
            && diagnostic.Message.Contains("indirect ABI", StringComparison.Ordinal)
            && diagnostic.Message.Contains("Big", StringComparison.Ordinal));
    }

    [Fact]
    public void TailLargeParameterUseFrontEndAbiDiagnostic()
    {
        var result = Compile(
            """
            module Demo

            struct Big
            {
                i64[min max] A;
                i64[min max] B;
                i64[min max] C;
            }

            tail fn i64[min max] Read(Big value)
            {
                return value.A;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "STK9999");
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "STK4121"
            && diagnostic.Stage == "lower-abi"
            && diagnostic.Message.Contains("parameter 'value'", StringComparison.Ordinal)
            && diagnostic.Message.Contains("indirect ABI", StringComparison.Ordinal));
    }
}
