using Stark.Compiler;

namespace compiler.Tests;

public sealed class TypeCheckingTests
{
    [Fact]
    public void IntegerExponentiationRequiresFloatingPointOperand()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run() {
                return 2 ** 3;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("floating-point operand", StringComparison.Ordinal));
    }

    [Fact]
    public void BitwiseXorRequiresIntegerOperands()
    {
        var result = Compile(
            """
            module Demo

            fn f32 Run() {
                return 1.0 ^ 2.0;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3002"
                && diagnostic.Message.Contains("integer operands", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitArithmeticOperatorsTypeCheckWithoutPlaceholderDiagnostics()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i32 left, i32 right) {
                stack mut i32 value = left;
                value +%= right;
                stack i32 product = left *| right;
                return -%value +% product +| 3;
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "STK3008");
    }

    [Fact]
    public void StrictFpModifierIsRejectedUntilLoweringExists()
    {
        var result = Compile(
            """
            module Demo

            strictfp fn f32 Run(f32 left, f32 right) {
                return left + right;
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "STK3008"
                && diagnostic.Message.Contains("strictfp", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitConversionsAndPointerOperatorsTypeCheck()
    {
        var result = Compile(
            """
            module Demo

            fn i32 Run(i64 bits, ascii text) {
                stack mut i32 value = 7;
                stack rawmutptr<i32> ptr = &value;
                stack rawptr<i32> readonlyPtr = (rawptr<i32>)ptr;
                *ptr = (i32)bits;
                stack i64 address = (i64)readonlyPtr;
                stack rawmutptr<i32> roundTrip = (rawmutptr<i32>)address;
                stack unicode wide = (unicode)text;
                stack ascii narrow = (ascii)wide;
                stack i32[2] values = { 1, 2 };
                stack i32[] view = (i32[])values;
                return *roundTrip + view[0];
            }
            """,
            new CompilerOptions(StopAfterPassId: "type-check"));

        Assert.True(result.Succeeded);
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.TypeCheckModel, out TypeCheckModel? typeCheckModel));
        Assert.NotNull(typeCheckModel);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }
}
