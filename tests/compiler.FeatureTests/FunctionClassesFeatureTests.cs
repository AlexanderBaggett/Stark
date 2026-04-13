namespace compiler.FeatureTests;

public sealed class FunctionClassesFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void FunctionClassesFlowThroughTheWholePipelineAndPreserveTheirLlvmShapes()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            fn i32[-2147483648 2147483647] Plain(i32[-2147483648 2147483647] left, i32[-2147483648 2147483647] right) {
                return left + right;
            }

            law i32[-2147483648 2147483647] Pure() {
                return Plain(1, 2);
            }

            finite i32[-2147483648 2147483647] Bounded() {
                return 4;
            }

            finite law i32[-2147483648 2147483647] Run() {
                return Pure() + Bounded();
            }
            """);

        Assert.Contains(
            "define fastcc i32 @Plain(i32 %arg_left, i32 %arg_right) nounwind willreturn mustprogress nosync nofree memory(none) alwaysinline",
            llvm);
        Assert.Contains("define fastcc i32 @Pure()", llvm);
        Assert.Contains("define fastcc i32 @Bounded()", llvm);
        Assert.Contains("define fastcc i32 @Run()", llvm);
    }
}
