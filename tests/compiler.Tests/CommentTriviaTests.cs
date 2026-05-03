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
                // fn i32[0 max] CommentedOutLine() {
                //     return 11;
                // }

                /*
                fn i32[0 max] CommentedOutBlock() {
                    return 22;
                }
                */

                /// <summary>
                /// fn i32[0 max] CommentedOutXmlLine() {
                ///     return 33;
                /// }
                /// </summary>
                /**
                 * <summary>
                 * fn i32[0 max] CommentedOutXmlBlock() {
                 *     return 44;
                 * }
                 * </summary>
                 */
                finite law i32[0 max] Run() {
                    // stack i32[0 max] commentedLocal = 55;
                    /*
                    stack i32[0 max] commentedBlockLocal = 66;
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
