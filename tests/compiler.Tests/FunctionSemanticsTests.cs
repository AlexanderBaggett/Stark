using Stark.Compiler;

namespace compiler.Tests;

public sealed class FunctionSemanticsTests
{
    [Fact]
    public void PlainFnsReportEffectiveKindsWhenTheyCanBeStrengthened()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            fn void Touch(borrow mut Box box) {
                box.Value = 1;
                return;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var add = validation.Functions["Add"];
        Assert.Equal(StarkFunctionKind.Fn, add.DeclaredKind);
        Assert.Equal(StarkFunctionKind.FiniteLaw, add.EffectiveKind);
        Assert.True(add.CanStrengthenKind);

        var touch = validation.Functions["Touch"];
        Assert.Equal(StarkFunctionKind.Fn, touch.DeclaredKind);
        Assert.Equal(StarkFunctionKind.Finite, touch.EffectiveKind);
        Assert.True(touch.CanStrengthenKind);
    }

    [Fact]
    public void PlainFnsWithWillexitLoopsCanStillStrengthenToFiniteKinds()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Count() {
                stack mut i32[-2147483648 2147483647] value = 0;

                while willexit (value < 3) {
                    value += 1;
                }

                return value;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var count = validation.Functions["Count"];
        Assert.Equal(StarkFunctionKind.Fn, count.DeclaredKind);
        Assert.Equal(StarkFunctionKind.FiniteLaw, count.EffectiveKind);
        Assert.True(count.CanStrengthenKind);
    }

    [Fact]
    public void SemanticValidationSummariesCaptureParameterGuaranteesAndReturnCaptures()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn retborrow Box Echo(retborrow Box value) {
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var echo = validation.Functions["Echo"];
        Assert.NotNull(echo.MemoryEffects);
        Assert.True(echo.MemoryEffects!.CapturesArgumentMemory);
        var parameter = Assert.Single(echo.Parameters!);
        Assert.Equal("value", parameter.Name);
        Assert.True(parameter.IsMemoryBacked);
        Assert.True(parameter.GuaranteedNonNull);
        Assert.True(parameter.GuaranteedReadOnly);
        Assert.Equal(4, parameter.DereferenceableBytes);
        Assert.Equal(4, parameter.AlignmentBytes);
        Assert.Equal(ParameterCaptureKind.Return, parameter.CaptureKind);
    }

    [Fact]
    public void SemanticValidationSummariesDeriveConcreteLayoutFactsForPaddedAggregates()
    {
        var result = Compile(
            """
            module Demo

            struct Pair {
                i8[-128 127] Tag;
                i32[-2147483648 2147483647] Value;
            }

            fn void Inspect(borrow Pair pair) {
                return;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var parameter = Assert.Single(validation.Functions["Inspect"].Parameters!);
        Assert.True(parameter.IsMemoryBacked);
        Assert.True(parameter.GuaranteedNonNull);
        Assert.Equal(8, parameter.DereferenceableBytes);
        Assert.Equal(4, parameter.AlignmentBytes);
    }

    [Fact]
    public void TransitiveCallEffectsFlowIntoSemanticSummaries()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            fn void Touch(borrow mut Box box) {
                box.Value = 1;
                return;
            }

            fn void Outer(borrow mut Box box) {
                Touch(box);
                return;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var outer = validation.Functions["Outer"];
        Assert.NotNull(outer.MemoryEffects);
        Assert.True(outer.MemoryEffects!.WritesArgumentMemory);
        var parameter = Assert.Single(outer.Parameters!);
        Assert.True(parameter.Writes);

        var call = Assert.Single(outer.Calls!);
        Assert.Equal("Touch", call.CalleeName);
        var argument = Assert.Single(call.Arguments);
        Assert.Equal("box", argument.CallerParameterName);
        Assert.Equal("box", argument.CalleeParameterName);
        Assert.True(argument.Writes);
    }

    [Fact]
    public void SemanticValidationSummariesDistinguishArgumentAndOtherMemory()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32[-2147483648 2147483647] Value;
            }

            static mut i32[-2147483648 2147483647] Counter = 0;

            fn i32[-2147483648 2147483647] ReadGlobal() {
                return Counter;
            }

            fn void TouchArg(borrow mut Box box) {
                box.Value = 1;
                return;
            }
            """);

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.SemanticValidation, out SemanticValidationModel? validation));
        Assert.NotNull(validation);

        var readGlobal = validation.Functions["ReadGlobal"];
        Assert.NotNull(readGlobal.MemoryEffects);
        Assert.True(readGlobal.MemoryEffects!.ReadsOtherMemory);
        Assert.False(readGlobal.MemoryEffects.WritesOtherMemory);
        Assert.False(readGlobal.MemoryEffects.ReadsArgumentMemory);

        var touchArg = validation.Functions["TouchArg"];
        Assert.NotNull(touchArg.MemoryEffects);
        Assert.True(touchArg.MemoryEffects!.WritesArgumentMemory);
        Assert.False(touchArg.MemoryEffects.ReadsOtherMemory);
        Assert.False(touchArg.MemoryEffects.WritesOtherMemory);
    }

    [Fact]
    public void LawsCanCallPlainFnsThatInferAsLaws()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Add(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            law i32[-2147483648 2147483647] Use() {
                return Add(1, 2);
            }
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void FiniteFunctionsCanCallPlainFnsThatInferAsFinite()
    {
        var result = Compile(
            """
            module Demo

            fn i32[-2147483648 2147483647] Step(i32[-2147483648 2147483647] value) {
                return value + 1;
            }

            finite i32[-2147483648 2147483647] Use(i32[-2147483648 2147483647] value) {
                return Step(value);
            }
            """);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void PureCallsInheritExternallyVisibleSliceWrites()
    {
        var result = Compile(
            """
            module Demo

            law void Touch(mut i32[-2147483648 2147483647][] view) {
                view[0] = 1;
                return;
            }

            law void Outer(mut i32[-2147483648 2147483647][] view) {
                Touch(view);
                return;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4104" && diagnostic.Message.Contains("Touch", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STK4104" && diagnostic.Message.Contains("through call 'Touch'", StringComparison.Ordinal));
    }

    private static CompilationResult Compile(string source)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source));
    }
}
