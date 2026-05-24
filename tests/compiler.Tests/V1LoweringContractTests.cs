using Stark.Compiler;

namespace compiler.Tests;

public sealed class V1LoweringContractTests
{
    [Fact]
    public void InternalScalarFunctionsUseFastccAndDirectScalarAbi()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Add(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc noundef i32 @Add(i32 noundef %arg_left, i32 noundef %arg_right)", llvm);
        Assert.DoesNotContain("define i32 @Add(", llvm, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalTextAndSliceValuesKeepConcretePtrLengthAbi()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn ascii Echo(ascii text)
            {
                return text;
            }

            unsafe fn unicode Wide(unicode text)
            {
                return text;
            }

            unsafe fn i32[min max] Read(i32[min max][] view, i32[min max] index)
            {
                return view[index];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("%stark_unicode = type { ptr, i64 }", llvm);
        Assert.Contains("define fastcc noundef %stark_ascii @Echo(%stark_ascii noundef %arg_text)", llvm);
        Assert.Contains("define fastcc noundef %stark_unicode @Wide(%stark_unicode noundef %arg_text)", llvm);
        Assert.Contains("define fastcc noundef i32 @Read({ ptr, i64 } noundef %arg_view, i32 noundef %arg_index)", llvm);
    }

    [Fact]
    public void FfiAsciiBoundaryDoesNotUseFastccAndLowersToPointerAbi()
    {
        var result = Compile(
            """
            module Demo

            unsafe ffi fn i32[min max] puts(ascii text);

            export unsafe fn i32[min max] main()
            {
                puts("Hello");
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("declare i32 @puts(ptr readonly)", llvm);
        Assert.Contains("i32 @main()", llvm);
        Assert.DoesNotContain("declare fastcc i32 @puts(", llvm);
        Assert.DoesNotContain("define fastcc i32 @main()", llvm);
    }

    [Fact]
    public void SafeExportedMainDoesNotNeedUnsafeOrFfiAndUsesNativeCallingConvention()
    {
        var result = Compile(
            """
            module Demo

            export fn i32[min max] main()
            {
                return 0;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define noundef i32 @main()", llvm);
        Assert.DoesNotContain("define fastcc noundef i32 @main()", llvm);
    }

    [Fact]
    public void ExportedStarkFunctionsUseNativeCallingConventionWithoutFfiUnsafe()
    {
        var result = Compile(
            """
            module Demo

            export fn i32[min max] AddOne(i32[min max] value)
            {
                return value + 1;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("i32 @AddOne(i32 noundef %arg_value)", llvm);
        Assert.DoesNotContain("define fastcc", llvm);
    }

    [Fact]
    public void BorrowedAggregatesRemainIndirectWithReadonlyFacts()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i8[min max] Tag;
                i32[min max] Value;
            }

            unsafe fn void Inspect(borrow Pair pair)
            {
                return;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc void @Inspect(ptr noundef nonnull noalias readonly nocapture dereferenceable(8) align 4 %arg_pair)", llvm);
        Assert.DoesNotContain("define fastcc void @Inspect(%Pair %arg_pair)", llvm);
    }

    [Fact]
    public void SixteenByteAggregatesStayDirectByValue()
    {
        var result = Compile(
            """
            module Demo

            struct Pair
            {
                i64[min max] Left;
                i64[min max] Right;
            }

            unsafe fn Pair Step(Pair value)
            {
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Pair = type { i64, i64 }", llvm);
        Assert.Contains("define fastcc noundef %Pair @Step(%Pair noundef %arg_value)", llvm);
        Assert.DoesNotContain("sret(%Pair)", llvm);
        Assert.DoesNotContain("byval(%Pair)", llvm);
    }

    [Fact]
    public void AggregatesLargerThanSixteenBytesUseIndirectAbi()
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

            unsafe fn Big Step(Big value)
            {
                return value;
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("%Big = type { i64, i64, i64 }", llvm);
        Assert.Contains("define fastcc void @Step(ptr noundef noalias sret(%Big) nonnull dereferenceable(24) align 8 %ret, ptr noundef nonnull byval(%Big) noalias readonly nocapture dereferenceable(24) align 8 %arg_value)", llvm);
        Assert.DoesNotContain("define fastcc %Big @Step(%Big %arg_value)", llvm);
    }

    [Fact]
    public void FixedArraysFollowTheSameSixteenByteThreshold()
    {
        var result = Compile(
            """
            module Demo

            unsafe fn i32[min max] Read4(i32[min max][4] values)
            {
                return values[0];
            }

            unsafe fn i32[min max] Read5(i32[min max][5] values)
            {
                return values[0];
            }
            """);

        Assert.True(result.Succeeded);
        var llvm = GetLlvm(result);

        Assert.Contains("define fastcc noundef i32 @Read4([4 x i32] noundef %arg_values)", llvm);
        Assert.DoesNotContain("byval([4 x i32])", llvm);
        Assert.Contains("define fastcc noundef i32 @Read5(", llvm);
        Assert.Contains("ptr noundef nonnull byval([5 x i32]) noalias readonly nocapture dereferenceable(20) align 4 %arg_values", llvm);
        Assert.DoesNotContain("define fastcc i32 @Read5([5 x i32] %arg_values)", llvm);
    }

    private static CompilationResult Compile(string source, CompilerOptions? options = null)
    {
        return DefaultCompilerPipeline.Create().Run(new CompilationInput(source), options);
    }

    private static string GetLlvm(CompilationResult result)
    {
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvmModule));
        Assert.NotNull(llvmModule);
        return llvmModule.Text;
    }
}
