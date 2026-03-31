namespace compiler.FeatureTests;

public sealed class BorrowingFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void BorrowParametersPreserveReadonlyPointerAbiThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            struct Box {
                i32 Value;
            }

            fn i32 Read(borrow Box box) {
                return box.Value;
            }
            """);

        Assert.Contains(
            "define fastcc i32 @Read(ptr nonnull noalias readonly nocapture dereferenceable(4) align 4 %arg_box)",
            llvm);
    }
}
