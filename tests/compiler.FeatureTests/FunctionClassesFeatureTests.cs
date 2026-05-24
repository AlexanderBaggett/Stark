namespace compiler.FeatureTests;

public sealed class FunctionClassesFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void FunctionClassesFlowThroughTheWholePipelineAndPreserveTheirLlvmShapes()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            fn i32[min max] Plain(i32[min max] left, i32[min max] right)
            {
                return left + right;
            }

            law i32[min max] Pure()
            {
                return Plain(1, 2);
            }

            finite i32[min max] Bounded()
            {
                return 4;
            }

            finite law i32[min max] Run()
            {
                return Pure() + Bounded();
            }
            """);

        Assert.Contains(
            "define fastcc noundef i32 @Plain(i32 noundef %arg_left, i32 noundef %arg_right) nounwind willreturn mustprogress nosync nofree memory(none) alwaysinline",
            llvm);
        Assert.Contains("define fastcc noundef i32 @Pure()", llvm);
        Assert.Contains("define fastcc noundef i32 @Bounded()", llvm);
        Assert.Contains("define fastcc noundef i32 @Run()", llvm);
    }
}
