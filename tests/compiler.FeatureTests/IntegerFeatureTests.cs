namespace compiler.FeatureTests;

public sealed class IntegerFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void ConstantIntegerExpressionsFoldThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law i32[min max] Run()
            {
                return 1 + 2;
            }
            """);

        Assert.Contains("define fastcc noundef i32 @Run()", llvm);
        Assert.Contains("ret i32 3", llvm);
        Assert.DoesNotContain("add i32", llvm);
    }
}
