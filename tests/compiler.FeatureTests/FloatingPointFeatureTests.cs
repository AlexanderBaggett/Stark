namespace compiler.FeatureTests;

public sealed class FloatingPointFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void ConstantFloatExpressionsFoldThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            fn f32 Run() {
                return 2.0 ** 3.0;
            }
            """);

        Assert.Contains("define fastcc float @Run()", llvm);
        Assert.Contains("ret float 8", llvm);
        Assert.DoesNotContain("@llvm.pow.f32", llvm);
    }
}
