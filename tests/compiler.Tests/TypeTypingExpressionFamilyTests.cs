using Stark.Compiler;

namespace compiler.Tests;

public sealed class TypeTypingExpressionFamilyTests
{
    [Fact]
    public void AssignmentExpressionsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 right) {
                stack mut i32 value = left;
                value = right;
                value += 1;
                value &= 3;
                return value;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        AssertTypedSuccessfully(result);
    }

    [Fact]
    public void UnaryAndExponentExpressionsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 value) {
                stack i32 negated = -value;
                stack i32 complemented = ~value;
                stack f32 powered = 2.0 ** 3.0;
                return negated + complemented;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        AssertTypedSuccessfully(result);
    }

    [Fact]
    public void MultiplicativeAdditiveAndShiftExpressionsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 right) {
                stack i32 mixed = left + right * 2 - 1;
                stack i32 shifted = mixed << 1 >> 1;
                return shifted;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        AssertTypedSuccessfully(result);
    }

    [Fact]
    public void BitwiseComparisonLogicalAndConditionalExpressionsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            fn bool Run(i32 left, i32 right, bool flag) {
                return ((left & right) == 0 && (left ^ right) != 1) || flag ? true : false;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        AssertTypedSuccessfully(result);
    }

    [Fact]
    public void PostfixCallsIndexesMembersAndObjectCreationTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Echo(i32 value) {
                return value;
            }

            fn i32 Run(Box box, i32[2] values, bool flag) {
                stack Box created = new Box() { Value = 1 };
                return flag ? Echo(box.Value + created.Value) : values[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        AssertTypedSuccessfully(result);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static void AssertTypedSuccessfully(CompilationResult result)
    {
        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }
}
