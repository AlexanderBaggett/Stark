namespace compiler.FeatureTests;

public sealed class StringsFeatureTests : FeatureLlvmTestBase
{
    [Fact]
    public void AsciiStringsUseConcreteRuntimeAbiThroughTheWholePipeline()
    {
        var llvm = CompileToLlvm(
            """
            module Demo

            finite law ascii Echo(ascii text) {
                return text;
            }

            finite law ascii Run() {
                return Echo("Hi");
            }
            """);

        Assert.Contains("%stark_ascii = type { ptr, i64 }", llvm);
        Assert.Contains("define fastcc %stark_ascii @Echo(%stark_ascii %arg_text)", llvm);
        Assert.Contains(
            "call %stark_ascii @Echo(%stark_ascii { ptr getelementptr inbounds ([3 x i8], ptr @.str.0, i32 0, i32 0), i64 2 })",
            llvm);
    }
}
