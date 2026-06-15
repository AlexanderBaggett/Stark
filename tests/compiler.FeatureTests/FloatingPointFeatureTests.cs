namespace compiler.FeatureTests;

public sealed class FloatingPointFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void ConstantFloatExpressionsFoldThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            fn f32 Run()
            {
                return 2.0 ** 3.0;
            }
            """);

        Assert.Contains("define fastcc noundef float @Run()", llvm);
        // 2.0 ** 3.0 folds to 8.0; f32 8.0 emits as a bit-exact hex float.
        Assert.Contains("ret float 0x4020000000000000", llvm);
        Assert.DoesNotContain("@llvm.pow.f32", llvm);
    }
}
