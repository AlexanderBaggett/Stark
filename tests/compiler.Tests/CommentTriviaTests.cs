using Stark.Compiler;

namespace compiler.Tests;

public sealed class CommentTriviaTests
{
    [Fact]
    public void LineBlockAndXmlCommentsAreIgnoredBeforeLowering()
    {
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                module Demo

                //stuff.
                // fn u32[0 2 ** 31 - 1] CommentedOutLine() {
                //     return 11;
                // }

                /*
                fn u32[0 2 ** 31 - 1] CommentedOutBlock() {
                    return 22;
                }
                */

                /// <summary>
                /// fn u32[0 2 ** 31 - 1] CommentedOutXmlLine() {
                ///     return 33;
                /// }
                /// </summary>
                /**
                 * <summary>
                 * fn u32[0 2 ** 31 - 1] CommentedOutXmlBlock() {
                 *     return 44;
                 * }
                 * </summary>
                 */
                finite law u32[0 2 ** 31 - 1] Run() {
                    // stack u32[0 2 ** 31 - 1] commentedLocal = 55;
                    /*
                    stack u32[0 2 ** 31 - 1] commentedBlockLocal = 66;
                    return commentedBlockLocal;
                    */
                    return 7;
                }
                """),
            new CompilerOptions(StopAfterPassId: "emit-llvm"));

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));

        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        Assert.Contains("define fastcc", llvm, StringComparison.Ordinal);
        Assert.Contains("@Run(", llvm, StringComparison.Ordinal);
        Assert.Contains("ret i32 7", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("CommentedOut", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("commentedLocal", llvm, StringComparison.Ordinal);
    }
}
