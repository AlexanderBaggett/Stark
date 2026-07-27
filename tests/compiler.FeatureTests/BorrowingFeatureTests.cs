namespace compiler.FeatureTests;

public sealed class BorrowingFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void BorrowParametersPreserveReadonlyPointerAbiThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box
            {
                i32[min max] Value;
            }

            fn i32[min max] Read(borrow Box box)
            {
                return box.Value;
            }
            """);

        Assert.Contains(
            "define fastcc noundef i32 @Read(ptr noundef nonnull noalias readonly captures(none) dereferenceable(4) align 4 %arg_box)",
            llvm);
    }
}
