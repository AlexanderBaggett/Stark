using Stark.Compiler;

namespace compiler.Tests;

public sealed class FunctionSemanticsTests
{
    [Fact]
    public void SemanticValidationSummariesCaptureParameterGuaranteesAndReturnCaptures()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
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
                i8 Tag;
                i32 Value;
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
                i32 Value;
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
    public void PureCallsInheritExternallyVisibleSliceWrites()
    {
        var result = Compile(
            """
            module Demo

            law void Touch(i32[] view) {
                view[0] = 1;
                return;
            }

            law void Outer(i32[] view) {
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
