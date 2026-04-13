namespace compiler.FeatureTests;

public sealed class FunctionKindsFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void LawFunctionEmitsLawShapedLlvmAttributes()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            law i32[-2147483648 2147483647] Run() {
                return 1;
            }
            """);

        Assert.Contains("define fastcc i32 @Run()", llvm);
        Assert.Contains("nounwind", llvm);
        Assert.Contains("nosync", llvm);
        Assert.Contains("nofree", llvm);
        Assert.Contains("memory(none)", llvm);
    }
}
